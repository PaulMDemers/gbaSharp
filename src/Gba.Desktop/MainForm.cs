using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Gba.Core;
using Gba.Core.Cartridges;
using Gba.Core.Input;
using Gba.Core.Memory;
using Gba.Core.Video;

namespace Gba.Desktop;

public sealed class MainForm : Form
{
    private static readonly TimeSpan FrameDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond * 1_000_000L / 59_727_500L);
    private readonly object _sync = new();
    private readonly PictureBox _display = new();
    private readonly ToolStripButton _runButton = new("Run");
    private readonly ToolStripButton _pauseButton = new("Pause");
    private readonly ToolStripButton _resetButton = new("Reset");
    private readonly ToolStripMenuItem _useBiosMenuItem = new("Use BIOS when available") { CheckOnClick = true, Checked = true };
    private readonly ToolStripStatusLabel _status = new("No ROM loaded");
    private readonly System.Windows.Forms.Timer _presentTimer = new();
    private readonly int[] _argbFrame = new int[VideoController.Pixels];
    private Bitmap _frontBitmap = new(VideoController.Width, VideoController.Height, PixelFormat.Format32bppArgb);
    private Bitmap _backBitmap = new(VideoController.Width, VideoController.Height, PixelFormat.Format32bppArgb);
    private GbaSystem? _gba;
    private byte[]? _bios;
    private string? _biosPath;
    private string? _romPath;
    private string? _savePath;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private volatile bool _newFrame;
    private long _emulatedFrames;
    private long _framesPresented;
    private long _lastFrameCounter;
    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();

    public MainForm()
    {
        Text = "gbaSharp";
        ClientSize = new Size(VideoController.Width * 3, VideoController.Height * 3 + 56);
        MinimumSize = new Size(VideoController.Width * 2, VideoController.Height * 2 + 96);
        KeyPreview = true;

        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("File");
        file.DropDownItems.Add("Open BIOS...", null, (_, _) => OpenBios());
        _useBiosMenuItem.CheckedChanged += (_, _) => ResetRom();
        file.DropDownItems.Add(_useBiosMenuItem);
        file.DropDownItems.Add("Open ROM...", null, (_, _) => OpenRom());
        file.DropDownItems.Add("Write Save", null, (_, _) => WriteSave());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("Exit", null, (_, _) => Close());
        menu.Items.Add(file);

        var toolbar = new ToolStrip();
        _runButton.Click += (_, _) => StartEmulation();
        _pauseButton.Click += (_, _) => PauseEmulation();
        _resetButton.Click += (_, _) => ResetRom();
        toolbar.Items.AddRange([_runButton, _pauseButton, _resetButton]);

        _display.Dock = DockStyle.Fill;
        _display.BackColor = Color.Black;
        _display.SizeMode = PictureBoxSizeMode.Zoom;
        _display.TabStop = false;

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);

        MainMenuStrip = menu;
        Controls.Add(_display);
        Controls.Add(statusStrip);
        Controls.Add(toolbar);
        Controls.Add(menu);
        menu.Dock = DockStyle.Top;
        toolbar.Dock = DockStyle.Top;
        statusStrip.Dock = DockStyle.Bottom;

        _presentTimer.Interval = 16;
        _presentTimer.Tick += (_, _) => PresentFrame();
        _presentTimer.Start();
        TryLoadDefaultBios();
        UpdateButtons();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = KeyFromKeys(keyData & Keys.KeyCode);
        if (key == GbaKey.None)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        SetKey(key, pressed: true);
        return true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        var key = KeyFromKeys(e.KeyCode);
        if (key != GbaKey.None)
        {
            SetKey(key, pressed: false);
            e.Handled = true;
            return;
        }

        base.OnKeyUp(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        PauseEmulation();
        WriteSave();
        _display.Image = null;
        _frontBitmap.Dispose();
        _backBitmap.Dispose();
        base.OnFormClosing(e);
    }

    private async void OpenRom()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Game Boy Advance ROMs (*.gba)|*.gba|All files (*.*)|*.*",
            Title = "Open GBA ROM"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        PauseEmulation();
        try
        {
            var cartridge = await Cartridge.LoadFileAsync(dialog.FileName);
            var gba = CreateSystem();
            gba.LoadCartridge(cartridge);
            gba.Video.VBlankStarted += () => CaptureFrame(gba);

            lock (_sync)
            {
                _gba = gba;
                _romPath = dialog.FileName;
                _savePath = Path.ChangeExtension(dialog.FileName, ".sav");
                _newFrame = true;
                _emulatedFrames = 0;
                _framesPresented = 0;
                _lastFrameCounter = 0;
                Array.Clear(_argbFrame);
            }

            LoadSave();
            StartEmulation();
            SetStatus(FormatStatus(cartridge));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Could not open ROM", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        UpdateButtons();
    }

    private void OpenBios()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Game Boy Advance BIOS (*.bin)|*.bin|All files (*.*)|*.*",
            Title = "Open GBA BIOS"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            LoadBios(dialog.FileName);
            if (!_useBiosMenuItem.Checked)
            {
                _useBiosMenuItem.Checked = true;
            }
            else if (_romPath is not null)
            {
                ResetRom();
            }
            else
            {
                SetStatus($"BIOS loaded: {Path.GetFileName(dialog.FileName)}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Could not open BIOS", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StartEmulation()
    {
        lock (_sync)
        {
            if (_gba is null || _runCancellation is not null)
            {
                UpdateButtons();
                return;
            }

            _runCancellation = new CancellationTokenSource();
            _runTask = Task.Run(() => RunLoop(_runCancellation.Token));
        }

        UpdateButtons();
    }

    private void PauseEmulation()
    {
        CancellationTokenSource? cancellation;
        Task? task;
        lock (_sync)
        {
            cancellation = _runCancellation;
            task = _runTask;
            _runCancellation = null;
            _runTask = null;
        }

        if (cancellation is null)
        {
            UpdateButtons();
            return;
        }

        cancellation.Cancel();
        try
        {
            task?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
        }
        finally
        {
            cancellation.Dispose();
        }

        UpdateButtons();
    }

    private void ResetRom()
    {
        if (_romPath is null)
        {
            return;
        }

        var shouldRun = _runCancellation is not null;
        PauseEmulation();
        try
        {
            var cartridge = Cartridge.Load(File.ReadAllBytes(_romPath));
            var gba = CreateSystem();
            gba.LoadCartridge(cartridge);
            gba.Video.VBlankStarted += () => CaptureFrame(gba);
            lock (_sync)
            {
                _gba = gba;
                _newFrame = true;
                _emulatedFrames = 0;
                _framesPresented = 0;
                _lastFrameCounter = 0;
            }

            LoadSave();
            if (shouldRun)
            {
                StartEmulation();
            }

            SetStatus(FormatStatus(cartridge));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Could not reset ROM", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RunLoop(CancellationToken cancellationToken)
    {
        var nextFrameTime = Stopwatch.GetTimestamp();
        var frameTicks = Math.Max(1, (long)(FrameDuration.TotalSeconds * Stopwatch.Frequency));
        var observedFrames = Interlocked.Read(ref _emulatedFrames);

        while (!cancellationToken.IsCancellationRequested)
        {
            GbaSystem? gba;
            lock (_sync)
            {
                gba = _gba;
                if (gba is null)
                {
                    return;
                }

                for (var i = 0; i < 4096 && !cancellationToken.IsCancellationRequested; i++)
                {
                    gba.Step();
                }
            }

            var currentFrames = Interlocked.Read(ref _emulatedFrames);
            if (currentFrames != observedFrames)
            {
                observedFrames = currentFrames;
                nextFrameTime += frameTicks;
                var delayTicks = nextFrameTime - Stopwatch.GetTimestamp();
                if (delayTicks > 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds((double)delayTicks / Stopwatch.Frequency));
                }
                else if (delayTicks < -frameTicks * 4)
                {
                    nextFrameTime = Stopwatch.GetTimestamp();
                }
            }

            Thread.Yield();
        }
    }

    private void CaptureFrame(GbaSystem gba)
    {
        var framebuffer = gba.Video.Framebuffer;
        for (var i = 0; i < framebuffer.Length; i++)
        {
            _argbFrame[i] = unchecked((int)(0xFF00_0000u | framebuffer[i]));
        }

        _newFrame = true;
        Interlocked.Increment(ref _emulatedFrames);
        Interlocked.Increment(ref _lastFrameCounter);
    }

    private void PresentFrame()
    {
        if (!_newFrame)
        {
            UpdateStatusFps();
            return;
        }

        Bitmap displayBitmap;
        lock (_sync)
        {
            _newFrame = false;
            var data = _backBitmap.LockBits(
                new Rectangle(0, 0, VideoController.Width, VideoController.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(_argbFrame, 0, data.Scan0, _argbFrame.Length);
            }
            finally
            {
                _backBitmap.UnlockBits(data);
            }

            displayBitmap = _backBitmap;
            _backBitmap = _frontBitmap;
            _frontBitmap = displayBitmap;
        }

        _display.Image = displayBitmap;
        _framesPresented++;
        UpdateStatusFps();
    }

    private void LoadSave()
    {
        lock (_sync)
        {
            if (_gba is null || _savePath is null || _gba.Bus.SaveDataSize == 0 || !File.Exists(_savePath))
            {
                return;
            }

            _gba.Bus.LoadSaveData(File.ReadAllBytes(_savePath));
        }
    }

    private void WriteSave()
    {
        lock (_sync)
        {
            if (_gba is null || _savePath is null || _gba.Bus.SaveDataSize == 0)
            {
                return;
            }

            File.WriteAllBytes(_savePath, _gba.Bus.ExportSaveData().ToArray());
        }
    }

    private void SetKey(GbaKey key, bool pressed)
    {
        lock (_sync)
        {
            if (_gba is null)
            {
                return;
            }

            if (pressed)
            {
                _gba.Keypad.Press(key);
            }
            else
            {
                _gba.Keypad.Release(key);
            }
        }
    }

    private void UpdateButtons()
    {
        var hasRom = _gba is not null;
        var running = _runCancellation is not null;
        _runButton.Enabled = hasRom && !running;
        _pauseButton.Enabled = running;
        _resetButton.Enabled = hasRom;
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
    }

    private void UpdateStatusFps()
    {
        if (_gba?.Cartridge is null || _fpsClock.ElapsedMilliseconds < 500)
        {
            return;
        }

        var frames = Interlocked.Exchange(ref _lastFrameCounter, 0);
        var fps = (frames * 1000.0) / Math.Max(1, _fpsClock.ElapsedMilliseconds);
        _fpsClock.Restart();
        _status.Text = $"{FormatStatus(_gba.Cartridge)}  {fps:0.0} fps";
    }

    private GbaSystem CreateSystem()
        => _bios is null || !_useBiosMenuItem.Checked ? new GbaSystem() : new GbaSystem(_bios);

    private void TryLoadDefaultBios()
    {
        foreach (var path in DefaultBiosCandidates())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                LoadBios(path);
                SetStatus($"BIOS loaded: {Path.GetFileName(path)}");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
    }

    private static IEnumerable<string> DefaultBiosCandidates()
    {
        var roots = AncestorRoots(AppContext.BaseDirectory)
            .Concat(AncestorRoots(Environment.CurrentDirectory))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var relativePaths = new[]
        {
            "gba_bios.bin",
            Path.Combine("bios", "gba_bios.bin"),
            Path.Combine("gba_collection", "Massive GBA - EverDrive GBA 2022-08-08", "5 Tools & Service Test Carts", "BIOS", "[BIOS] Game Boy Advance (World).bin")
        };

        foreach (var root in roots)
        {
            foreach (var relativePath in relativePaths)
            {
                yield return Path.GetFullPath(Path.Combine(root, relativePath));
            }
        }
    }

    private static IEnumerable<string> AncestorRoots(string path)
    {
        var directory = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(directory))
        {
            yield return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }
    }

    private void LoadBios(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != GbaMemoryMap.BiosSize)
        {
            throw new ArgumentException($"A GBA BIOS must be {GbaMemoryMap.BiosSize} bytes; this file is {bytes.Length} bytes.", nameof(path));
        }

        if (bytes.All(value => value == 0))
        {
            throw new ArgumentException("The selected BIOS file is all zero bytes.", nameof(path));
        }

        _bios = bytes;
        _biosPath = path;
    }

    private string FormatStatus(Cartridge cartridge)
    {
        var bios = _biosPath is null
            ? "No BIOS"
            : _useBiosMenuItem.Checked ? $"BIOS: {Path.GetFileName(_biosPath)}" : "BIOS disabled";
        return $"{Display(cartridge.Header.Title)} ({Display(cartridge.Header.GameCode)})  {bios}";
    }

    private static GbaKey KeyFromKeys(Keys key)
        => key switch
        {
            Keys.Z => GbaKey.A,
            Keys.X => GbaKey.B,
            Keys.Enter => GbaKey.Start,
            Keys.Back or Keys.RShiftKey or Keys.LShiftKey or Keys.ShiftKey => GbaKey.Select,
            Keys.Right => GbaKey.Right,
            Keys.Left => GbaKey.Left,
            Keys.Up => GbaKey.Up,
            Keys.Down => GbaKey.Down,
            Keys.S => GbaKey.R,
            Keys.A => GbaKey.L,
            _ => GbaKey.None
        };

    private static string Display(string value)
        => string.IsNullOrWhiteSpace(value) ? "(blank)" : value.Trim();
}
