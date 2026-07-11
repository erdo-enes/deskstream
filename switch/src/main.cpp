#include <iostream>
#include <string>
#include <vector>
#include <map>
#include <chrono>
#include <thread>
#include <algorithm>
#include <fstream>
#include <cstring>
#include <sys/socket.h>
#include <netinet/in.h>
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
                
                HidVibrationDeviceHandle handles[2];
                hidInitializeVibrationDevices(handles, 2, HidNpadIdType_No1, HidNpadStyleTag_NpadJoyDual);
                
                HidVibrationValue values[2];
                // Left joy-con
                values[0].amp_low = largeMotor / 255.0f;
                values[0].freq_low = 160.0f;
                values[0].amp_high = 0.0f;
                values[0].freq_high = 320.0f;
                
                // Right joy-con
                values[1].amp_low = 0.0f;
                values[1].freq_low = 160.0f;
                values[1].amp_high = smallMotor / 255.0f;
                values[1].freq_high = 320.0f;
                
                hidSendVibrationValues(handles, values, 2);
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

static const uint8_t font8x8[95][8] = {
    {0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00}, //  
    {0x18, 0x3C, 0x3C, 0x18, 0x18, 0x00, 0x18, 0x00}, // !
    {0x36, 0x36, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00}, // "
    {0x24, 0x24, 0x7E, 0x24, 0x7E, 0x24, 0x24, 0x00}, // #
    {0x08, 0x3E, 0x08, 0x08, 0x3E, 0x08, 0x08, 0x00}, // $
    {0x24, 0x54, 0x08, 0x08, 0x10, 0x2A, 0x24, 0x00}, // %
    {0x18, 0x24, 0x18, 0x14, 0x22, 0x22, 0x1C, 0x00}, // &
    {0x18, 0x18, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00}, // '
    {0x08, 0x10, 0x20, 0x20, 0x20, 0x10, 0x08, 0x00}, // (
    {0x10, 0x08, 0x04, 0x04, 0x04, 0x08, 0x10, 0x00}, // )
    {0x00, 0x24, 0x18, 0x7E, 0x18, 0x24, 0x00, 0x00}, // *
    {0x00, 0x08, 0x08, 0x3E, 0x08, 0x08, 0x00, 0x00}, // +
    {0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x18, 0x08}, // ,
    {0x00, 0x00, 0x00, 0x3E, 0x00, 0x00, 0x00, 0x00}, // -
    {0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x18, 0x00}, // .
    {0x00, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x00}, // /
    {0x3C, 0x42, 0x42, 0x42, 0x42, 0x42, 0x3C, 0x00}, // 0
    {0x18, 0x28, 0x08, 0x08, 0x08, 0x08, 0x3E, 0x00}, // 1
    {0x3C, 0x42, 0x02, 0x3C, 0x40, 0x40, 0x7E, 0x00}, // 2
    {0x3C, 0x42, 0x0C, 0x02, 0x02, 0x42, 0x3C, 0x00}, // 3
    {0x08, 0x18, 0x28, 0x48, 0x7E, 0x08, 0x08, 0x00}, // 4
    {0x7E, 0x40, 0x7C, 0x02, 0x02, 0x42, 0x3C, 0x00}, // 5
    {0x3C, 0x40, 0x7C, 0x42, 0x42, 0x42, 0x3C, 0x00}, // 6
    {0x7E, 0x02, 0x04, 0x08, 0x10, 0x10, 0x10, 0x00}, // 7
    {0x3C, 0x42, 0x42, 0x3C, 0x42, 0x42, 0x3C, 0x00}, // 8
    {0x3C, 0x42, 0x42, 0x3E, 0x02, 0x02, 0x3C, 0x00}, // 9
    {0x00, 0x18, 0x18, 0x00, 0x18, 0x18, 0x00, 0x00}, // :
    {0x00, 0x18, 0x18, 0x00, 0x18, 0x18, 0x08, 0x00}, // ;
    {0x04, 0x08, 0x10, 0x20, 0x10, 0x08, 0x04, 0x00}, // <
    {0x00, 0x00, 0x3E, 0x00, 0x3E, 0x00, 0x00, 0x00}, // =
    {0x20, 0x10, 0x08, 0x04, 0x08, 0x10, 0x20, 0x00}, // >
    {0x3C, 0x42, 0x02, 0x0C, 0x10, 0x00, 0x10, 0x00}, // ?
    {0x3C, 0x42, 0x5A, 0x52, 0x5A, 0x40, 0x3C, 0x00}, // @
    {0x18, 0x24, 0x42, 0x42, 0x7E, 0x42, 0x42, 0x00}, // A
    {0x7C, 0x42, 0x42, 0x7C, 0x42, 0x42, 0x7C, 0x00}, // B
    {0x3C, 0x42, 0x40, 0x40, 0x40, 0x42, 0x3C, 0x00}, // C
    {0x78, 0x44, 0x42, 0x42, 0x42, 0x44, 0x78, 0x00}, // D
    {0x7E, 0x40, 0x40, 0x78, 0x40, 0x40, 0x7E, 0x00}, // E
    {0x7E, 0x40, 0x40, 0x78, 0x40, 0x40, 0x40, 0x00}, // F
    {0x3C, 0x42, 0x40, 0x4E, 0x42, 0x42, 0x3C, 0x00}, // G
    {0x42, 0x42, 0x42, 0x7E, 0x42, 0x42, 0x42, 0x00}, // H
    {0x3E, 0x08, 0x08, 0x08, 0x08, 0x08, 0x3E, 0x00}, // I
    {0x02, 0x02, 0x02, 0x02, 0x02, 0x42, 0x3C, 0x00}, // J
    {0x42, 0x44, 0x48, 0x70, 0x48, 0x44, 0x42, 0x00}, // K
    {0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x7E, 0x00}, // L
    {0x42, 0x66, 0x5A, 0x42, 0x42, 0x42, 0x42, 0x00}, // M
    {0x42, 0x62, 0x52, 0x4A, 0x46, 0x42, 0x42, 0x00}, // N
    {0x3C, 0x42, 0x42, 0x42, 0x42, 0x42, 0x3C, 0x00}, // O
    {0x7C, 0x42, 0x42, 0x7C, 0x40, 0x40, 0x40, 0x00}, // P
    {0x3C, 0x42, 0x42, 0x42, 0x4A, 0x44, 0x3A, 0x00}, // Q
    {0x7C, 0x42, 0x42, 0x7C, 0x48, 0x44, 0x42, 0x00}, // R
    {0x3C, 0x42, 0x40, 0x3C, 0x02, 0x42, 0x3C, 0x00}, // S
    {0x7E, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x00}, // T
    {0x42, 0x42, 0x42, 0x42, 0x42, 0x42, 0x3C, 0x00}, // U
    {0x42, 0x42, 0x42, 0x42, 0x24, 0x24, 0x18, 0x00}, // V
    {0x42, 0x42, 0x42, 0x5A, 0x5A, 0x66, 0x42, 0x00}, // W
    {0x42, 0x24, 0x18, 0x18, 0x24, 0x24, 0x42, 0x00}, // X
    {0x42, 0x42, 0x24, 0x18, 0x08, 0x08, 0x08, 0x00}, // Y
    {0x7E, 0x02, 0x04, 0x08, 0x10, 0x20, 0x7E, 0x00}, // Z
    {0x3C, 0x20, 0x20, 0x20, 0x20, 0x20, 0x3C, 0x00}, // [
    {0x00, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x00}, // Backslash
    {0x3C, 0x02, 0x02, 0x02, 0x02, 0x02, 0x3C, 0x00}, // ]
    {0x08, 0x14, 0x22, 0x00, 0x00, 0x00, 0x00, 0x00}, // ^
    {0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x7E, 0x00}, // _
    {0x08, 0x08, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00}, // `
    {0x00, 0x00, 0x3C, 0x02, 0x3E, 0x42, 0x3E, 0x00}, // a
    {0x40, 0x40, 0x7C, 0x42, 0x42, 0x42, 0x7C, 0x00}, // b
    {0x00, 0x00, 0x3C, 0x40, 0x40, 0x42, 0x3C, 0x00}, // c
    {0x02, 0x02, 0x3E, 0x42, 0x42, 0x42, 0x3E, 0x00}, // d
    {0x00, 0x00, 0x3C, 0x42, 0x7E, 0x40, 0x3C, 0x00}, // e
    {0x1C, 0x22, 0x20, 0x78, 0x20, 0x20, 0x20, 0x00}, // f
    {0x00, 0x00, 0x3A, 0x46, 0x46, 0x3E, 0x02, 0x3C}, // g
    {0x40, 0x40, 0x7C, 0x42, 0x42, 0x42, 0x42, 0x00}, // h
    {0x08, 0x00, 0x18, 0x08, 0x08, 0x08, 0x1C, 0x00}, // i
    {0x04, 0x00, 0x0C, 0x04, 0x04, 0x04, 0x44, 0x38}, // j
    {0x40, 0x40, 0x44, 0x48, 0x70, 0x48, 0x44, 0x00}, // k
    {0x18, 0x08, 0x08, 0x08, 0x08, 0x08, 0x1C, 0x00}, // l
    {0x00, 0x00, 0x66, 0x5A, 0x42, 0x42, 0x42, 0x00}, // m
    {0x00, 0x00, 0x7C, 0x42, 0x42, 0x42, 0x42, 0x00}, // n
    {0x00, 0x00, 0x3C, 0x42, 0x42, 0x42, 0x3C, 0x00}, // o
    {0x00, 0x00, 0x7C, 0x42, 0x42, 0x7C, 0x40, 0x40}, // p
    {0x00, 0x00, 0x3E, 0x42, 0x42, 0x3E, 0x02, 0x02}, // q
    {0x00, 0x00, 0x5C, 0x62, 0x40, 0x40, 0x40, 0x00}, // r
    {0x00, 0x00, 0x3E, 0x40, 0x3C, 0x02, 0x3C, 0x00}, // s
    {0x20, 0x20, 0x78, 0x20, 0x20, 0x20, 0x18, 0x00}, // t
    {0x00, 0x00, 0x42, 0x42, 0x42, 0x42, 0x3E, 0x00}, // u
    {0x00, 0x00, 0x42, 0x42, 0x24, 0x24, 0x18, 0x00}, // v
    {0x00, 0x00, 0x42, 0x42, 0x5A, 0x5A, 0x24, 0x00}, // w
    {0x00, 0x00, 0x42, 0x24, 0x18, 0x24, 0x42, 0x00}, // x
    {0x00, 0x00, 0x42, 0x42, 0x3E, 0x02, 0x3C, 0x40}, // y
    {0x00, 0x00, 0x7E, 0x04, 0x08, 0x10, 0x7E, 0x00}, // z
    {0x0C, 0x10, 0x10, 0x20, 0x10, 0x10, 0x0C, 0x00}, // {
    {0x08, 0x08, 0x08, 0x00, 0x08, 0x08, 0x08, 0x00}, // |
    {0x30, 0x08, 0x08, 0x04, 0x08, 0x08, 0x30, 0x00}, // }
    {0x00, 0x00, 0x00, 0x24, 0x58, 0x00, 0x00, 0x00}  // ~
};

static void drawChar(SDL_Renderer* r, char c, int x, int y, int scale, SDL_Color color) {
    if (c < 32 || c > 127) c = '?';
    const uint8_t* glyph = font8x8[c - 32];
    SDL_SetRenderDrawColor(r, color.r, color.g, color.b, color.a);
    for (int row = 0; row < 8; ++row) {
        for (int col = 0; col < 8; ++col) {
            if (glyph[row] & (1 << (7 - col))) {
                SDL_Rect rect = { x + col * scale, y + row * scale, scale, scale };
                SDL_RenderFillRect(r, &rect);
            }
        }
    }
}

static void drawText(SDL_Renderer* r, const std::string& text, int x, int y, int scale, SDL_Color color) {
    int curX = x;
    for (char c : text) {
        if (c == '\n') {
            y += 8 * scale + 4;
            curX = x;
        } else {
            drawChar(r, c, curX, y, scale, color);
            curX += 8 * scale;
        }
    }
}

struct MenuItem {
    std::string label;
    int type; // 0: saved, 1: discovered, 2: manual, 3: exit
    int index; // index in discovered list
};

int main(int argc, char* argv[]) {
    (void)argc; (void)argv;
    std::cout << "[main] Initializing DeskStream Switch Homebrew client." << std::endl;

    if (SDL_Init(SDL_INIT_VIDEO | SDL_INIT_AUDIO | SDL_INIT_JOYSTICK) < 0) {
        std::cerr << "SDL_Init failed: " << SDL_GetError() << std::endl;
        return -1;
    }

    // Open all available controllers
    int numJoysticks = SDL_NumJoysticks();
    std::vector<SDL_Joystick*> openJoysticks;
    for (int i = 0; i < numJoysticks; ++i) {
        SDL_Joystick* j = SDL_JoystickOpen(i);
        if (j) openJoysticks.push_back(j);
    }

    SDL_Window* window = SDL_CreateWindow("DeskStream", SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED, 1280, 720, 0);
    SDL_Renderer* renderer = SDL_CreateRenderer(window, -1, SDL_RENDERER_ACCELERATED | SDL_RENDERER_PRESENTVSYNC);

    if (!window || !renderer) {
        std::cerr << "Failed to create SDL window/renderer." << std::endl;
        for (auto* j : openJoysticks) SDL_JoystickClose(j);
        SDL_Quit();
        return -1;
    }

    SavedConfig cfg = loadConfig();
    std::string ip = cfg.serverIp;

    DiscoveryClient discovery;
    discovery.start();

    std::vector<DiscoveredServer> list;
    uint32_t lastProbeTicks = 0;
    int selectIdx = 0;

    bool quit = false;
    while (!quit) {
        // 1. Poll LAN Discovery automatically (every 1.5 seconds)
        uint32_t now = SDL_GetTicks();
        if (now - lastProbeTicks >= 1500) {
            auto freshList = discovery.probe();
            if (!freshList.empty() || list.empty()) {
                list = freshList;
            }
            lastProbeTicks = now;
        }

        // 2. Build Menu Items dynamically
        std::vector<MenuItem> menuItems;
        if (!cfg.serverIp.empty()) {
            menuItems.push_back({"Connect to Saved Server (" + cfg.serverIp + ")", 0, 0});
        }
        for (size_t i = 0; i < list.size(); ++i) {
            menuItems.push_back({"Discovered: " + list[i].hostname + " (" + list[i].ip + ")", 1, (int)i});
        }
        menuItems.push_back({"Connect to Manual IP Address", 2, 0});
        menuItems.push_back({"Exit", 3, 0});

        if (selectIdx >= (int)menuItems.size()) {
            selectIdx = (int)menuItems.size() - 1;
        }
        if (selectIdx < 0) {
            selectIdx = 0;
        }

        // 3. Process Input events
        bool activateItem = false;
        bool triggerManual = false;
        bool triggerRefresh = false;

        SDL_Event event;
        while (SDL_PollEvent(&event)) {
            if (event.type == SDL_QUIT) {
                quit = true;
            } else if (event.type == SDL_KEYDOWN) {
                switch (event.key.keysym.sym) {
                    case SDLK_UP:
                    case SDLK_w:
                        selectIdx = (selectIdx - 1 + menuItems.size()) % menuItems.size();
                        break;
                    case SDLK_DOWN:
                    case SDLK_s:
                        selectIdx = (selectIdx + 1) % menuItems.size();
                        break;
                    case SDLK_RETURN:
                    case SDLK_SPACE:
                        activateItem = true;
                        break;
                    case SDLK_ESCAPE:
                        quit = true;
                        break;
                    case SDLK_y:
                    case SDLK_x:
                        triggerManual = true;
                        break;
                    case SDLK_r:
                        triggerRefresh = true;
                        break;
                }
            } else if (event.type == SDL_JOYBUTTONDOWN) {
                int button = event.jbutton.button;
                // standard Joy-Con mapping:
                // 0: A, 1: B, 2: X, 3: Y, 10: Plus, 11: Minus, 13: D-pad Up, 15: D-pad Down
                if (button == 13) {
                    selectIdx = (selectIdx - 1 + menuItems.size()) % menuItems.size();
                } else if (button == 15) {
                    selectIdx = (selectIdx + 1) % menuItems.size();
                } else if (button == 0) { // A
                    activateItem = true;
                } else if (button == 2 || button == 3) { // X / Y -> Manual IP
                    triggerManual = true;
                } else if (button == 10) { // Plus -> Refresh
                    triggerRefresh = true;
                } else if (button == 11) { // Minus -> Exit
                    quit = true;
                }
            } else if (event.type == SDL_JOYHATMOTION) {
                if (event.jhat.value & SDL_HAT_UP) {
                    selectIdx = (selectIdx - 1 + menuItems.size()) % menuItems.size();
                } else if (event.jhat.value & SDL_HAT_DOWN) {
                    selectIdx = (selectIdx + 1) % menuItems.size();
                }
            }
        }

        // 4. Handle Actions
        if (triggerRefresh) {
            list.clear();
            lastProbeTicks = 0;
        }

        if (triggerManual) {
            std::string manualIp = showKeyboard("Enter Windows PC IP Address", ip);
            if (!manualIp.empty()) {
                ip = manualIp;
                cfg.token = ""; // reset token
                runStream(renderer, ip, 47801, "");
                cfg = loadConfig();
                ip = cfg.serverIp;
            }
        }

        if (activateItem && selectIdx < (int)menuItems.size()) {
            MenuItem item = menuItems[selectIdx];
            if (item.type == 0) {
                runStream(renderer, cfg.serverIp, 47801, cfg.token);
                cfg = loadConfig();
                ip = cfg.serverIp;
            } else if (item.type == 1) {
                ip = list[item.index].ip;
                cfg.token = ""; // reset token for new server
                runStream(renderer, ip, list[item.index].controlPort, "");
                cfg = loadConfig();
                ip = cfg.serverIp;
            } else if (item.type == 2) {
                std::string manualIp = showKeyboard("Enter Windows PC IP Address", ip);
                if (!manualIp.empty()) {
                    ip = manualIp;
                    cfg.token = ""; // reset token
                    runStream(renderer, ip, 47801, "");
                    cfg = loadConfig();
                    ip = cfg.serverIp;
                }
            } else if (item.type == 3) {
                quit = true;
            }
        }

        // 5. Render graphical menu
        SDL_SetRenderDrawColor(renderer, 23, 27, 33, 255);
        SDL_RenderClear(renderer);

        // Header
        drawText(renderer, "DeskStream", 60, 50, 4, {255, 255, 255, 255});
        drawText(renderer, "Low-Latency Windows Stream Client", 60, 90, 2, {140, 150, 165, 255});

        // Horizontal separator line
        SDL_Rect separator = { 60, 120, 1160, 2 };
        SDL_SetRenderDrawColor(renderer, 50, 55, 65, 255);
        SDL_RenderFillRect(renderer, &separator);

        // Server List / Options
        int startY = 160;
        for (size_t i = 0; i < menuItems.size(); ++i) {
            bool selected = ((int)i == selectIdx);
            SDL_Rect card = { 60, startY + (int)i * 65, 1160, 50 };
            if (selected) {
                SDL_SetRenderDrawColor(renderer, 59, 130, 246, 255); // Blue highlight
            } else {
                SDL_SetRenderDrawColor(renderer, 35, 41, 50, 255); // Dark card
            }
            SDL_RenderFillRect(renderer, &card);

            SDL_Color textColor = selected ? SDL_Color{255, 255, 255, 255} : SDL_Color{210, 220, 235, 255};
            drawText(renderer, menuItems[i].label, 80, startY + (int)i * 65 + 17, 2, textColor);
        }

        // Status message
        if (list.empty()) {
            drawText(renderer, "Scanning LAN for DeskStream servers...", 60, startY + (int)menuItems.size() * 65 + 20, 2, {154, 164, 176, 255});
        } else {
            drawText(renderer, "Scan active. Discovered " + std::to_string(list.size()) + " server(s).", 60, startY + (int)menuItems.size() * 65 + 20, 2, {34, 197, 94, 255});
        }

        // Footer Instructions
        SDL_Rect footerLine = { 60, 640, 1160, 1 };
        SDL_SetRenderDrawColor(renderer, 50, 55, 65, 255);
        SDL_RenderFillRect(renderer, &footerLine);

        drawText(renderer, "Nav: D-pad / L-Stick | Select: A / Enter | Manual IP: X / Y | Refresh: Plus / R | Exit: Minus / ESC", 60, 660, 2, {140, 150, 165, 255});

        SDL_RenderPresent(renderer);
        std::this_thread::sleep_for(std::chrono::milliseconds(16)); // ~60fps UI loop
    }

    // Teardown
    SDL_DestroyRenderer(renderer);
    SDL_DestroyWindow(window);
    for (auto* j : openJoysticks) {
        if (SDL_JoystickGetAttached(j)) {
            SDL_JoystickClose(j);
        }
    }
    SDL_Quit();
    return 0;
}
