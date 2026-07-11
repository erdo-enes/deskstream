#include <iostream>
#include <string>
#include <vector>
#include <map>
#include <chrono>
#include <thread>
#include <algorithm>
#include <fstream>
#include <cstring>
#include <arpa/inet.h>
#include <SDL2/SDL.h>

#include "network.hpp"
#include "fec.hpp"
#include "decoder.hpp"
#include "audio.hpp"
#include "input.hpp"

#ifdef __SWITCH__
#include <switch.h>
#endif

// Simple, dependency-free config storage
struct SavedConfig {
    std::string serverIp;
    std::string token;
};

static SavedConfig loadConfig() {
    SavedConfig cfg;
    std::ifstream f("paired_config.json");
    if (f.is_open()) {
        std::getline(f, cfg.serverIp);
        std::getline(f, cfg.token);
    }
    return cfg;
}

static void saveConfig(const std::string& ip, const std::string& token) {
    std::ofstream f("paired_config.json");
    if (f.is_open()) {
        f << ip << "\n" << token << "\n";
    }
}

// Helper to prompt user with Switch virtual keyboard
static std::string showKeyboard(const std::string& guide, const std::string& initialText = "") {
#ifdef __SWITCH__
    SwkbdConfig kbd;
    char tmp[128] = {0};
    Result rc = swkbdCreate(&kbd, 0);
    if (R_SUCCEEDED(rc)) {
        swkbdConfigMakePresetDefault(&kbd);
        swkbdConfigSetGuideText(&kbd, guide.c_str());
        swkbdConfigSetInitialText(&kbd, initialText.c_str());
        swkbdShow(&kbd, tmp, sizeof(tmp));
        swkbdClose(&kbd);
    }
    return std::string(tmp);
#else
    std::cout << guide << " [Initial: " << initialText << "]: ";
    std::string val;
    std::getline(std::cin, val);
    return val.empty() ? initialText : val;
#endif
}

// Simple JSON extraction helper
static std::string getJsonValue(const std::string& json, const std::string& key) {
    size_t kPos = json.find("\"" + key + "\"");
    if (kPos == std::string::npos) return "";
    size_t colon = json.find(":", kPos);
    if (colon == std::string::npos) return "";
    size_t start = json.find_first_not_of(" \t\r\n", colon + 1);
    if (start == std::string::npos) return "";
    
    if (json[start] == '"') { // string value
        size_t end = json.find("\"", start + 1);
        if (end == std::string::npos) return "";
        return json.substr(start + 1, end - (start + 1));
    } else { // integer / boolean value
        size_t end = json.find_first_of(",}", start);
        if (end == std::string::npos) return "";
        return json.substr(start, end - start);
    }
}

// Run streaming session
static void runStream(SDL_Renderer* renderer, const std::string& ip, uint16_t port, const std::string& token) {
    TCPClient control(ip, port);
    std::cout << "[stream] Connecting to control " << ip << ":" << port << "..." << std::endl;
    if (!control.connectToServer()) {
        std::cerr << "[stream] Control connection failed." << std::endl;
        return;
    }

    // Send HELLO
    std::string clientName = "Switch";
#ifdef __SWITCH__
    clientName = "Nintendo Switch Console";
#endif
    std::string helloJson = "{\"type\":\"HELLO\",\"ver\":1,\"clientId\":\"switch-unique-id\",\"clientName\":\"" + clientName + "\",\"token\":\"" + token + "\"}";
    control.sendMessage(helloJson);

    std::string response;
    if (!control.readMessage(response, 5000)) {
        std::cerr << "[stream] HELLO handshake timeout." << std::endl;
        return;
    }

    std::string respType = getJsonValue(response, "type");
    std::string activeToken = token;

    if (respType == "PAIR_REQUIRED") {
        std::cout << "[stream] Pairing required." << std::endl;
        control.sendMessage("{\"type\":\"PAIR_REQUEST\"}");
        
        std::string pin = showKeyboard("Enter 6-digit PIN shown on the server");
        if (pin.length() != 6) {
            std::cerr << "[stream] Invalid PIN length." << std::endl;
            return;
        }
        
        control.sendMessage("{\"type\":\"PAIR_CODE\",\"pin\":\"" + pin + "\"}");
        
        std::string pairResp;
        if (!control.readMessage(pairResp, 5000)) {
            std::cerr << "[stream] Pairing response timeout." << std::endl;
            return;
        }
        
        if (getJsonValue(pairResp, "type") == "PAIR_OK") {
            activeToken = getJsonValue(pairResp, "token");
            std::cout << "[stream] Pairing successful! Token: " << activeToken << std::endl;
            saveConfig(ip, activeToken);
            
            // Reconnect
            control.disconnect();
            std::this_thread::sleep_for(std::chrono::milliseconds(500));
            runStream(renderer, ip, port, activeToken);
            return;
        } else {
            std::cerr << "[stream] Pairing failed." << std::endl;
            return;
        }
    }

    if (respType != "HELLO_OK") {
        std::cerr << "[stream] Handshake rejected: " << response << std::endl;
        return;
    }

    std::cout << "[stream] Handshake completed successfully." << std::endl;

    // Send START_STREAM
    // Request 720p as it is the native portable resolution for Switch
    std::string startJson = "{\"type\":\"START_STREAM\",\"maxBitrateKbps\":10000,\"fps\":60,\"quality\":\"720p\"}";
    control.sendMessage(startJson);

    std::string startResp;
    if (!control.readMessage(startResp, 5000)) {
        std::cerr << "[stream] START_STREAM response timeout." << std::endl;
        return;
    }

    if (getJsonValue(startResp, "type") != "STREAM_STARTED") {
        std::cerr << "[stream] Failed to start stream: " << startResp << std::endl;
        return;
    }

    int mediaPort = std::stoi(getJsonValue(startResp, "mediaPort"));
    int width = std::stoi(getJsonValue(startResp, "width"));
    int height = std::stoi(getJsonValue(startResp, "height"));

    std::cout << "[stream] Stream started at " << width << "x" << height << ", UDP media port: " << mediaPort << std::endl;

    // Bind UDP receivers
    UDPReceiver mediaSocket;
    if (!mediaSocket.bindToAny()) {
        std::cerr << "[stream] Failed to bind media UDP socket." << std::endl;
        return;
    }
    control.sendMessage("{\"type\":\"MEDIA_READY\",\"port\":" + std::to_string(mediaSocket.getPort()) + "}");

    // Request audio
    control.sendMessage("{\"type\":\"AUDIO_START\"}");
    UDPReceiver audioSocket;
    bool audioEnabled = false;

    std::string audioResp;
    if (control.readMessage(audioResp, 2000)) {
        if (getJsonValue(audioResp, "type") == "AUDIO_STARTED") {
            int audioPort = std::stoi(getJsonValue(audioResp, "audioPort"));
            if (audioSocket.bindToAny()) {
                control.sendMessage("{\"type\":\"AUDIO_READY\",\"port\":" + std::to_string(audioSocket.getPort()) + "}");
                audioEnabled = true;
                std::cout << "[stream] Audio started, UDP audio port: " << audioPort << std::endl;
            }
        }
    }

    // Start controllers & inputs
    control.sendMessage("{\"type\":\"GAMEPAD_START\",\"controllers\":1}");
    control.sendMessage("{\"type\":\"INPUT_START\",\"mouse\":false,\"keyboard\":false}");

    // Setup destination addresses for hole punch / gamepad
    struct sockaddr_in destMediaAddr;
    std::memset(&destMediaAddr, 0, sizeof(destMediaAddr));
    destMediaAddr.sin_family = AF_INET;
    destMediaAddr.sin_port = htons(mediaPort);
    inet_pton(AF_INET, ip.c_str(), &destMediaAddr.sin_addr);

    // Decoder, Audio, Input setup
    H264Decoder decoder;
    if (!decoder.init(width, height)) {
        std::cerr << "[stream] Failed to initialize H.264 decoder." << std::endl;
        return;
    }

    AudioPlayer audioPlayer;
    if (audioEnabled) {
        audioPlayer.init();
    }

    InputManager input;
    input.init();

    // Create SDL Texture for YUV rendering
    SDL_Texture* texture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_IYUV, SDL_TEXTUREACCESS_STREAMING, width, height);
    if (!texture) {
        std::cerr << "[stream] Failed to create SDL Texture." << std::endl;
        return;
    }

    // Assembly queues
    std::map<uint32_t, FrameAssembly> assemblyQueue;
    uint32_t latestCompleteFrameId = 0xFFFFFFFF;
    uint32_t latestReceivedFrameId = 0xFFFFFFFF;
    
    // Stats tracking
    auto lastStatsTime = std::chrono::steady_clock::now();
    uint32_t statsFramesOk = 0;
    uint32_t statsFramesDropped = 0;
    uint32_t statsBytes = 0;

    auto lastHolePunchTime = std::chrono::steady_clock::now();
    auto lastInputTime = std::chrono::steady_clock::now();

    uint8_t packetBuf[1500];
    bool running = true;

    std::cout << "[stream] Entering active stream loop." << std::endl;

    while (running && control.isConnected()) {
        // 1. Process local UI events
        SDL_Event event;
        while (SDL_PollEvent(&event)) {
            if (event.type == SDL_QUIT) {
                running = false;
                break;
            } else if (event.type == SDL_KEYDOWN) {
                if (event.key.keysym.sym == SDLK_ESCAPE) {
                    running = false;
                    break;
                }
            }
        }

        auto now = std::chrono::steady_clock::now();

        // 2. Periodic hole punch (every 1s)
        if (std::chrono::duration_cast<std::chrono::seconds>(now - lastHolePunchTime).count() >= 1) {
            mediaSocket.sendTo((const uint8_t*)"DSMH", 4, destMediaAddr);
            if (audioEnabled) {
                struct sockaddr_in destAudioAddr = destMediaAddr;
                destAudioAddr.sin_port = htons(std::stoi(getJsonValue(audioResp, "audioPort")));
                audioSocket.sendTo((const uint8_t*)"DSAH", 4, destAudioAddr);
            }
            lastHolePunchTime = now;
        }

        // 3. Controller polling & UDP gamepad updates
        GamepadPacket gpPacket;
        bool forceGpHeartbeat = std::chrono::duration_cast<std::chrono::milliseconds>(now - lastInputTime).count() >= 200;
        if (input.pollInput(gpPacket, forceGpHeartbeat)) {
            mediaSocket.sendTo(reinterpret_cast<const uint8_t*>(&gpPacket), sizeof(gpPacket), destMediaAddr);
            lastInputTime = now;
        }

        // 4. Period Stats reports (every 1s)
        if (std::chrono::duration_cast<std::chrono::seconds>(now - lastStatsTime).count() >= 1) {
            std::string statsJson = "{\"type\":\"STATS\",\"framesOk\":" + std::to_string(statsFramesOk) + 
                                    ",\"framesDropped\":" + std::to_string(statsFramesDropped) + 
                                    ",\"bytes\":" + std::to_string(statsBytes) + 
                                    ",\"intervalMs\":1000,\"captureToReceiveP95Ms\":10,\"decodeToSurfaceP95Ms\":5}";
            control.sendMessage(statsJson);
            statsFramesOk = 0;
            statsFramesDropped = 0;
            statsBytes = 0;
            lastStatsTime = now;
        }

        // 5. Check TCP control notifications (e.g. Keepalive, PING, RUMBLE)
        std::string controlMsg;
        if (control.readMessage(controlMsg, 0)) {
            std::string type = getJsonValue(controlMsg, "type");
            if (type == "PING") {
                control.sendMessage("{\"type\":\"PONG\"}");
            } else if (type == "GAMEPAD_RUMBLE") {
#ifdef __SWITCH__
                // Trigger HD Rumble if compiled on the console
                int largeMotor = std::stoi(getJsonValue(controlMsg, "largeMotor"));
                int smallMotor = std::stoi(getJsonValue(controlMsg, "smallMotor"));
                // Simple rumble activation
                hidSendVibrationValues(HidVibrationDeviceHandle_ControllerPlayer1_Left, largeMotor > 0 ? 0.5f : 0.0f, largeMotor > 0 ? 160.0f : 0.0f);
                hidSendVibrationValues(HidVibrationDeviceHandle_ControllerPlayer1_Right, smallMotor > 0 ? 0.5f : 0.0f, smallMotor > 0 ? 320.0f : 0.0f);
#endif
            } else if (type == "STREAM_STOPPED") {
                running = false;
            }
        }

        // 6. Receive and drain UDP Video packets
        struct sockaddr_in fromAddr;
        int readBytes = 0;
        while ((readBytes = mediaSocket.receive(packetBuf, sizeof(packetBuf), fromAddr, 0)) > 0) {
            statsBytes += readBytes;
            MediaPacket pkt;
            if (FecProcessor::parsePacket(packetBuf, readBytes, pkt)) {
                if (pkt.flags & 2) { // Liveness heartbeat DSHB
                    continue;
                }

                // If this is a new frame, initialize its assembly state
                if (assemblyQueue.find(pkt.frameId) == assemblyQueue.end()) {
                    // Check if it's too old
                    if (latestCompleteFrameId != 0xFFFFFFFF && pkt.frameId <= latestCompleteFrameId) {
                        continue;
                    }
                    FrameAssembly fa;
                    fa.frameId = pkt.frameId;
                    fa.packetCount = pkt.packetCount;
                    fa.fecCount = pkt.fecCount;
                    fa.ptsMs = pkt.ptsMs;
                    fa.isKeyframe = (pkt.flags & 1) != 0;
                    assemblyQueue[pkt.frameId] = fa;
                    latestReceivedFrameId = std::max(latestReceivedFrameId, pkt.frameId);
                }

                FrameAssembly& fa = assemblyQueue[pkt.frameId];
                if (pkt.flags & 2) { // Parity packet
                    fa.parityPackets[pkt.packetIndex] = pkt.payload;
                } else { // Data packet
                    fa.dataPackets[pkt.packetIndex] = pkt.payload;
                }

                // Run FEC recovery
                if (fa.runFecRecovery()) {
                    // Frame completed! Let's decode it immediately
                    latestCompleteFrameId = pkt.frameId;

                    // Assemble the Annex-B byte stream
                    std::vector<uint8_t> frameData;
                    for (uint16_t idx = 0; idx < fa.packetCount; ++idx) {
                        const auto& chunk = fa.dataPackets[idx];
                        frameData.insert(frameData.end(), chunk.begin(), chunk.end());
                    }

                    // Decode
                    uint8_t *yPlane = nullptr, *uPlane = nullptr, *vPlane = nullptr;
                    int yPitch = 0, uPitch = 0, vPitch = 0;
                    if (decoder.decode(frameData.data(), frameData.size(), yPlane, uPlane, vPlane, yPitch, uPitch, vPitch)) {
                        // Render frame
                        SDL_UpdateYUVTexture(texture, nullptr, yPlane, yPitch, uPlane, uPitch, vPlane, vPitch);
                        SDL_RenderClear(renderer);
                        SDL_RenderCopy(renderer, texture, nullptr, nullptr);
                        SDL_RenderPresent(renderer);
                        statsFramesOk++;
                    }
                    
                    // Clean up old frames in the queue
                    while (!assemblyQueue.empty() && assemblyQueue.begin()->first <= latestCompleteFrameId) {
                        assemblyQueue.erase(assemblyQueue.begin());
                    }
                }
            }
        }

        // 7. Check for unrecoverable frame drops (if we have gaps in frame assembly)
        if (latestReceivedFrameId != 0xFFFFFFFF && latestCompleteFrameId != 0xFFFFFFFF &&
            latestReceivedFrameId > latestCompleteFrameId + 2) {
            
            // Gap detected! Request an IDR frame to recover stream
            control.sendMessage("{\"type\":\"REQUEST_IDR\"}");
            statsFramesDropped++;
            
            // Purge assembly queues and reset frame track
            assemblyQueue.clear();
            latestCompleteFrameId = latestReceivedFrameId;
        }

        // 8. Receive and drain UDP Audio packets
        if (audioEnabled) {
            uint8_t audioBuf[1200];
            int audioBytes = 0;
            while ((audioBytes = audioSocket.receive(audioBuf, sizeof(audioBuf), fromAddr, 0)) > 0) {
                if (audioBytes >= 16) {
                    uint16_t payloadLen = (audioBuf[2] << 8) | audioBuf[3];
                    if (audioBytes >= 16 + payloadLen) {
                        audioPlayer.play(audioBuf + 16, payloadLen);
                    }
                }
            }
        }

        // Throttle active spin-wait slightly
        std::this_thread::sleep_for(std::chrono::microseconds(500));
    }

    // Teardown
    control.sendMessage("{\"type\":\"STOP_STREAM\"}");
    SDL_DestroyTexture(texture);
    std::cout << "[stream] Session disconnected." << std::endl;
}

int main(int argc, char* argv[]) {
    (void)argc; (void)argv;
    std::cout << "[main] Initializing DeskStream Switch Homebrew client." << std::endl;

    if (SDL_Init(SDL_INIT_VIDEO | SDL_INIT_AUDIO) < 0) {
        std::cerr << "SDL_Init failed: " << SDL_GetError() << std::endl;
        return -1;
    }

    SDL_Window* window = SDL_CreateWindow("DeskStream", SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED, 1280, 720, 0);
    SDL_Renderer* renderer = SDL_CreateRenderer(window, -1, SDL_RENDERER_ACCELERATED | SDL_RENDERER_PRESENTVSYNC);

    if (!window || !renderer) {
        std::cerr << "Failed to create SDL window/renderer." << std::endl;
        SDL_Quit();
        return -1;
    }

    SavedConfig cfg = loadConfig();
    std::string ip = cfg.serverIp;

    bool quit = false;
    while (!quit) {
        SDL_Event event;
        while (SDL_PollEvent(&event)) {
            if (event.type == SDL_QUIT) {
                quit = true;
            }
        }

        // Simple text-based display interface for the Main Menu using standard console 
        // or SDL rendering of choices. We will prompt the user to input server IP.
        std::cout << "\n=== DeskStream Client Menu ===" << std::endl;
        std::cout << "1. Connect to Saved Server (" << (ip.empty() ? "None" : ip) << ")" << std::endl;
        std::cout << "2. Discover Servers on LAN" << std::endl;
        std::cout << "3. Connect to Manual IP" << std::endl;
        std::cout << "4. Exit" << std::endl;

        std::string choice = showKeyboard("Select choice (1-4)", "1");
        if (choice == "1") {
            if (ip.empty()) {
                std::cout << "No saved server IP. Enter one first." << std::endl;
                continue;
            }
            runStream(renderer, ip, 47801, cfg.token);
        } else if (choice == "2") {
            DiscoveryClient discovery;
            if (discovery.start()) {
                std::cout << "Scanning LAN for DeskStream servers..." << std::endl;
                auto list = discovery.probe();
                if (list.empty()) {
                    std::cout << "No servers found." << std::endl;
                } else {
                    std::cout << "Discovered servers:" << std::endl;
                    for (size_t i = 0; i < list.size(); ++i) {
                        std::cout << i + 1 << ". " << list[i].hostname << " (" << list[i].ip << ")" << std::endl;
                    }
                    std::string select = showKeyboard("Select server index (1-" + std::to_string(list.size()) + ")", "1");
                    try {
                        size_t idx = std::stoi(select) - 1;
                        if (idx < list.size()) {
                            ip = list[idx].ip;
                            cfg.token = ""; // reset token for new server
                            runStream(renderer, ip, list[idx].controlPort, "");
                        }
                    } catch (...) {}
                }
            }
        } else if (choice == "3") {
            std::string manualIp = showKeyboard("Enter Windows PC IP Address", ip);
            if (!manualIp.empty()) {
                ip = manualIp;
                cfg.token = ""; // reset token
                runStream(renderer, ip, 47801, "");
            }
        } else if (choice == "4" || choice.empty()) {
            quit = true;
        }

        SDL_RenderClear(renderer);
        // Display a nice dark background with text instruction on Switch screen
        SDL_SetRenderDrawColor(renderer, 23, 27, 33, 255);
        SDL_RenderClear(renderer);
        SDL_RenderPresent(renderer);
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
    }

    SDL_DestroyRenderer(renderer);
    SDL_DestroyWindow(window);
    SDL_Quit();
    return 0;
}
