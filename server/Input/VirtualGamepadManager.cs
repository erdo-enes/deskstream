using DeskStreamer.Server.Protocol;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace DeskStreamer.Server.Input;

/// <summary>
/// Owns up to four ViGEm-backed virtual Xbox 360 controllers. The lock coordinates the
/// TCP control thread, UDP input thread, and ViGEm feedback callback thread.
/// </summary>
public sealed class VirtualGamepadManager : IDisposable
{
    public const int MaxControllers = 4;

    private readonly object _gate = new();
    private readonly List<IXbox360Controller> _controllers = new(MaxControllers);
    private readonly uint[] _lastSequence = new uint[MaxControllers];
    private readonly bool[] _hasSequence = new bool[MaxControllers];
    private ViGEmClient? _client;

    public event Action<int, byte, byte>? Rumble;

    public int Count
    {
        get { lock (_gate) return _controllers.Count; }
    }

    public int Start(int requestedCount)
    {
        int targetCount = Math.Clamp(requestedCount, 1, MaxControllers);
        lock (_gate)
        {
            _client ??= new ViGEmClient();

            while (_controllers.Count < targetCount)
            {
                int controllerId = _controllers.Count;
                IXbox360Controller controller = _client.CreateXbox360Controller();
                controller.AutoSubmitReport = false;
                controller.FeedbackReceived += (_, e) =>
                    Rumble?.Invoke(controllerId, e.LargeMotor, e.SmallMotor);
                try
                {
                    controller.Connect();
                    controller.ResetReport();
                    controller.SubmitReport();
                    _controllers.Add(controller);
                    _hasSequence[controllerId] = false;
                }
                catch
                {
                    DisposeController(controller);
                    if (_controllers.Count == 0)
                        DisposeClient();
                    throw;
                }
            }

            while (_controllers.Count > targetCount)
            {
                int last = _controllers.Count - 1;
                DisposeController(_controllers[last]);
                _controllers.RemoveAt(last);
                _hasSequence[last] = false;
            }

            return _controllers.Count;
        }
    }

    /// <summary>Applies a newest-wins state snapshot. Returns false for stale/unknown ids.</summary>
    public bool Apply(in GamepadState state)
    {
        lock (_gate)
        {
            int id = state.ControllerId;
            if (id < 0 || id >= _controllers.Count)
                return false;

            if (_hasSequence[id])
            {
                // Signed subtraction is the standard wrap-safe uint32 ordering test.
                int delta = unchecked((int)(state.Sequence - _lastSequence[id]));
                if (delta <= 0)
                    return false;
            }

            _lastSequence[id] = state.Sequence;
            _hasSequence[id] = true;

            IXbox360Controller controller = _controllers[id];
            controller.SetButtonsFull(state.Buttons);
            controller.SetSliderValue(Xbox360Slider.LeftTrigger, state.LeftTrigger);
            controller.SetSliderValue(Xbox360Slider.RightTrigger, state.RightTrigger);
            controller.SetAxisValue(Xbox360Axis.LeftThumbX, state.LeftX);
            controller.SetAxisValue(Xbox360Axis.LeftThumbY, state.LeftY);
            controller.SetAxisValue(Xbox360Axis.RightThumbX, state.RightX);
            controller.SetAxisValue(Xbox360Axis.RightThumbY, state.RightY);
            controller.SubmitReport();
            return true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            for (int i = _controllers.Count - 1; i >= 0; i--)
                DisposeController(_controllers[i]);
            _controllers.Clear();
            Array.Clear(_hasSequence);
            DisposeClient();
        }
    }

    private static void DisposeController(IXbox360Controller controller)
    {
        try
        {
            controller.ResetReport();
            controller.SubmitReport();
        }
        catch { }
        try { controller.Disconnect(); } catch { }
        try { (controller as IDisposable)?.Dispose(); } catch { }
    }

    private void DisposeClient()
    {
        try { _client?.Dispose(); } catch { }
        _client = null;
    }

    public void Dispose() => Stop();
}
