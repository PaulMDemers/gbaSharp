using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Gba.Core;
using Gba.Core.Cartridges;
using Gba.Core.Input;
using Gba.Core.Memory;
using Gba.Core.Video;

namespace Gba.Desktop;

public sealed class MainForm : Form
{
    private const int MaxRecentRoms = 8;
    private static readonly TimeSpan FrameDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond * 1_000_000L / 59_727_500L);
    private readonly object _sync = new();
    private readonly PictureBox _display = new();
    private readonly ToolStripButton _runButton = new("Run");
    private readonly ToolStripButton _pauseButton = new("Pause");
    private readonly ToolStripButton _frameStepButton = new("Frame");
    private readonly ToolStripButton _resetButton = new("Reset");
    private readonly ToolStripButton _audioButton = new("Audio") { CheckOnClick = true, Checked = true };
    private readonly ToolStripMenuItem _useBiosMenuItem = new("Use BIOS when available") { CheckOnClick = true, Checked = true };
    private readonly ToolStripMenuItem _recentRomsMenuItem = new("Recent ROMs");
    private readonly ToolStripMenuItem _writeSaveMenuItem = new("Write Save");
    private readonly ToolStripMenuItem _autosaveMenuItem = new("Autosave") { CheckOnClick = true, Checked = true };
    private readonly ToolStripMenuItem _screenshotMenuItem = new("Save Screenshot...");
    private readonly ToolStripMenuItem _pauseResumeMenuItem = new("Pause");
    private readonly ToolStripMenuItem _frameStepMenuItem = new("Step Frame");
    private readonly ToolStripMenuItem _resetMenuItem = new("Reset");
    private readonly ToolStripMenuItem _speedMenuItem = new("Speed");
    private readonly ToolStripStatusLabel _status = new("No ROM loaded");
    private readonly System.Windows.Forms.Timer _presentTimer = new();
    private readonly System.Windows.Forms.Timer _autosaveTimer = new();
    private readonly WaveOutAudioOutput _audioOutput = new();
    private readonly int[] _argbFrame = new int[VideoController.Pixels];
    private readonly string? _startupRomPath;
    private readonly DesktopSettings _settings;
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
    private double _speedMultiplier = 1.0;
    private bool _unlimitedSpeed;
    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();

    public MainForm(string? startupRomPath = null)
    {
        _startupRomPath = string.IsNullOrWhiteSpace(startupRomPath) ? null : startupRomPath;
        _settings = DesktopSettings.Load();
        Text = "gbaSharp";
        ClientSize = new Size(VideoController.Width * 3, VideoController.Height * 3 + 56);
        MinimumSize = new Size(VideoController.Width * 2, VideoController.Height * 2 + 96);
        KeyPreview = true;
        AllowDrop = true;

        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("File");
        var openBiosMenuItem = new ToolStripMenuItem("Open BIOS...", null, (_, _) => OpenBios())
        {
            ShortcutKeys = Keys.Control | Keys.B
        };
        file.DropDownItems.Add(openBiosMenuItem);
        _useBiosMenuItem.CheckedChanged += (_, _) => ResetRom();
        file.DropDownItems.Add(_useBiosMenuItem);
        var openRomMenuItem = new ToolStripMenuItem("Open ROM...", null, (_, _) => OpenRom())
        {
            ShortcutKeys = Keys.Control | Keys.O
        };
        file.DropDownItems.Add(openRomMenuItem);
        file.DropDownItems.Add(_recentRomsMenuItem);
        _writeSaveMenuItem.Click += (_, _) => WriteSave();
        _writeSaveMenuItem.ShortcutKeys = Keys.Control | Keys.S;
        file.DropDownItems.Add(_writeSaveMenuItem);
        file.DropDownItems.Add(_autosaveMenuItem);
        _screenshotMenuItem.Click += (_, _) => SaveScreenshot();
        _screenshotMenuItem.ShortcutKeys = Keys.F9;
        file.DropDownItems.Add(_screenshotMenuItem);
        file.DropDownItems.Add(new ToolStripSeparator());
        var exitMenuItem = new ToolStripMenuItem("Exit", null, (_, _) => Close())
        {
            ShortcutKeys = Keys.Alt | Keys.F4
        };
        file.DropDownItems.Add(exitMenuItem);
        menu.Items.Add(file);

        var emulation = new ToolStripMenuItem("Emulation");
        _pauseResumeMenuItem.Click += (_, _) => TogglePause();
        _pauseResumeMenuItem.ShortcutKeyDisplayString = "Space";
        _frameStepMenuItem.Click += (_, _) => StepFrame();
        _frameStepMenuItem.ShortcutKeys = Keys.Control | Keys.F;
        _resetMenuItem.Click += (_, _) => ResetRom();
        _resetMenuItem.ShortcutKeys = Keys.F5;
        emulation.DropDownItems.Add(_pauseResumeMenuItem);
        emulation.DropDownItems.Add(_frameStepMenuItem);
        emulation.DropDownItems.Add(_resetMenuItem);
        emulation.DropDownItems.Add(new ToolStripSeparator());
        AddSpeedMenuItem("1x", 1.0, unlimited: false, checkedByDefault: true);
        AddSpeedMenuItem("2x", 2.0, unlimited: false);
        AddSpeedMenuItem("3x", 3.0, unlimited: false);
        AddSpeedMenuItem("Unlimited", 1.0, unlimited: true);
        emulation.DropDownItems.Add(_speedMenuItem);
        menu.Items.Add(emulation);

        var toolbar = new ToolStrip();
        _runButton.Click += (_, _) => StartEmulation();
        _pauseButton.Click += (_, _) => PauseEmulation();
        _frameStepButton.Click += (_, _) => StepFrame();
        _resetButton.Click += (_, _) => ResetRom();
        _audioButton.CheckedChanged += (_, _) =>
        {
            _audioOutput.Enabled = _audioButton.Checked;
            UpdateAudioButton();
        };
        toolbar.Items.AddRange([_runButton, _pauseButton, _frameStepButton, _resetButton, new ToolStripSeparator(), _audioButton]);

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
        _autosaveTimer.Interval = 15_000;
        _autosaveTimer.Tick += (_, _) =>
        {
            if (_autosaveMenuItem.Checked)
            {
                WriteSave(quiet: true);
            }
        };
        _autosaveTimer.Start();
        LoadPersistedSettings();
        TryLoadDefaultBios();
        RefreshRecentRomsMenu();
        UpdateAudioButton();
        UpdateButtons();
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        Shown += (_, _) => OpenStartupRomIfNeeded();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (HandleShortcut(keyData))
        {
            return true;
        }

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
        WriteSave(quiet: true);
        SavePersistedSettings();
        _display.Image = null;
        _frontBitmap.Dispose();
        _backBitmap.Dispose();
        _audioOutput.Dispose();
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

        await OpenRomPathAsync(dialog.FileName);
    }

    private async Task OpenRomPathAsync(string path)
    {
        PauseEmulation();
        WriteSave(quiet: true);
        try
        {
            var fullPath = Path.GetFullPath(path);
            var cartridge = await Cartridge.LoadFileAsync(fullPath);
            var gba = CreateSystem();
            gba.LoadCartridge(cartridge);
            gba.Video.VBlankStarted += () => CaptureFrame(gba);
            gba.Audio.SampleProduced += _audioOutput.Enqueue;
            gba.Audio.PsgSampleProduced += _audioOutput.Enqueue;
            _audioOutput.Clear();

            lock (_sync)
            {
                _gba = gba;
                _romPath = fullPath;
                _savePath = Path.ChangeExtension(fullPath, ".sav");
                _newFrame = true;
                _emulatedFrames = 0;
                _framesPresented = 0;
                _lastFrameCounter = 0;
                Array.Clear(_argbFrame);
            }

            LoadSave();
            RememberRecentRom(fullPath);
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
            _settings.BiosPath = _biosPath;
            SavePersistedSettings();
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
            _audioOutput.Clear();
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
        WriteSave(quiet: true);
        try
        {
            var cartridge = Cartridge.Load(File.ReadAllBytes(_romPath));
            var gba = CreateSystem();
            gba.LoadCartridge(cartridge);
            gba.Video.VBlankStarted += () => CaptureFrame(gba);
            gba.Audio.SampleProduced += _audioOutput.Enqueue;
            gba.Audio.PsgSampleProduced += _audioOutput.Enqueue;
            _audioOutput.Clear();
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

    private void TogglePause()
    {
        if (_runCancellation is null)
        {
            StartEmulation();
        }
        else
        {
            PauseEmulation();
        }
    }

    private void StepFrame()
    {
        const int MaxStepsPerFrame = 1_000_000;

        if (_gba is null)
        {
            return;
        }

        if (_runCancellation is not null)
        {
            SetStatus("Pause before stepping a single frame.");
            return;
        }

        var startFrame = Interlocked.Read(ref _emulatedFrames);
        var steps = 0;
        lock (_sync)
        {
            if (_gba is null)
            {
                return;
            }

            while (Interlocked.Read(ref _emulatedFrames) == startFrame && steps < MaxStepsPerFrame)
            {
                _gba.Step();
                steps++;
            }
        }

        if (steps >= MaxStepsPerFrame)
        {
            SetStatus("Frame step stopped before VBlank; the ROM may be stalled.");
            return;
        }

        PresentFrame();
        UpdateStatusFps();
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
                if (!_unlimitedSpeed)
                {
                    var targetFrameTicks = Math.Max(1, (long)(frameTicks / Math.Max(0.1, _speedMultiplier)));
                    nextFrameTime += targetFrameTicks;
                    var delayTicks = nextFrameTime - Stopwatch.GetTimestamp();
                    if (delayTicks > 0)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds((double)delayTicks / Stopwatch.Frequency));
                    }
                    else if (delayTicks < -targetFrameTicks * 4)
                    {
                        nextFrameTime = Stopwatch.GetTimestamp();
                    }
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

    private void WriteSave(bool quiet = false)
    {
        var wrote = false;
        lock (_sync)
        {
            if (_gba is null || _savePath is null || _gba.Bus.SaveDataSize == 0)
            {
                return;
            }

            File.WriteAllBytes(_savePath, _gba.Bus.ExportSaveData().ToArray());
            wrote = true;
        }

        if (wrote && !quiet && _savePath is not null)
        {
            SetStatus($"Save written: {Path.GetFileName(_savePath)}");
        }
    }

    private void SaveScreenshot()
    {
        if (_gba is null)
        {
            return;
        }

        var suggestedName = _romPath is null
            ? "gbaSharp-screenshot.png"
            : $"{Path.GetFileNameWithoutExtension(_romPath)}-{DateTime.Now:yyyyMMdd-HHmmss}.png";
        using var dialog = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png|All files (*.*)|*.*",
            FileName = suggestedName,
            Title = "Save Screenshot"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        Bitmap snapshot;
        lock (_sync)
        {
            snapshot = (Bitmap)_frontBitmap.Clone();
        }

        using (snapshot)
        {
            snapshot.Save(dialog.FileName, ImageFormat.Png);
        }

        SetStatus($"Screenshot saved: {Path.GetFileName(dialog.FileName)}");
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
        _frameStepButton.Enabled = hasRom && !running;
        _resetButton.Enabled = hasRom;
        _writeSaveMenuItem.Enabled = hasRom;
        _autosaveMenuItem.Enabled = hasRom;
        _screenshotMenuItem.Enabled = hasRom;
        _pauseResumeMenuItem.Enabled = hasRom;
        _pauseResumeMenuItem.Text = running ? "Pause" : "Run";
        _frameStepMenuItem.Enabled = hasRom && !running;
        _resetMenuItem.Enabled = hasRom;
    }

    private void UpdateAudioButton()
    {
        if (!_audioOutput.IsAvailable)
        {
            _audioButton.Checked = false;
            _audioButton.Enabled = false;
            _audioButton.ToolTipText = _audioOutput.LastError is null
                ? "Audio output is unavailable."
                : $"Audio output is unavailable: {_audioOutput.LastError}";
            return;
        }

        _audioButton.Enabled = true;
        _audioButton.ToolTipText = _audioButton.Checked
            ? "Direct sound audio is enabled."
            : "Direct sound audio is muted.";
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
        _status.Text = $"{FormatStatus(_gba.Cartridge)}  {fps:0.0} fps  frame {_emulatedFrames:N0}  {SpeedLabel()}";
    }

    private GbaSystem CreateSystem()
        => _bios is null || !_useBiosMenuItem.Checked ? new GbaSystem() : new GbaSystem(_bios);

    private void TryLoadDefaultBios()
    {
        if (!string.IsNullOrWhiteSpace(_settings.BiosPath) && File.Exists(_settings.BiosPath))
        {
            try
            {
                LoadBios(_settings.BiosPath);
                SetStatus($"BIOS loaded: {Path.GetFileName(_settings.BiosPath)}");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _settings.BiosPath = null;
                SavePersistedSettings();
            }
        }

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
        _biosPath = Path.GetFullPath(path);
    }

    private string FormatStatus(Cartridge cartridge)
    {
        var bios = _biosPath is null
            ? "No BIOS"
            : _useBiosMenuItem.Checked ? $"BIOS: {Path.GetFileName(_biosPath)}" : "BIOS disabled";
        return $"{Display(cartridge.Header.Title)} ({Display(cartridge.Header.GameCode)})  {bios}";
    }

    private void AddSpeedMenuItem(string text, double multiplier, bool unlimited, bool checkedByDefault = false)
    {
        var item = new ToolStripMenuItem(text)
        {
            CheckOnClick = true,
            Checked = checkedByDefault,
            Tag = new SpeedChoice(multiplier, unlimited)
        };
        item.Click += (_, _) => SetSpeed(item);
        _speedMenuItem.DropDownItems.Add(item);
    }

    private void SetSpeed(ToolStripMenuItem selected)
    {
        foreach (ToolStripMenuItem item in _speedMenuItem.DropDownItems)
        {
            item.Checked = ReferenceEquals(item, selected);
        }

        var choice = (SpeedChoice)selected.Tag!;
        _speedMultiplier = choice.Multiplier;
        _unlimitedSpeed = choice.Unlimited;
        _settings.SpeedMultiplier = _speedMultiplier;
        _settings.UnlimitedSpeed = _unlimitedSpeed;
        SavePersistedSettings();
        _audioOutput.Clear();
        UpdateStatusFps();
    }

    private string SpeedLabel() => _unlimitedSpeed ? "unlimited" : $"{_speedMultiplier:0.#}x";

    private void LoadPersistedSettings()
    {
        _useBiosMenuItem.Checked = _settings.UseBios;
        _autosaveMenuItem.Checked = _settings.Autosave;
        _speedMultiplier = _settings.SpeedMultiplier <= 0 ? 1.0 : _settings.SpeedMultiplier;
        _unlimitedSpeed = _settings.UnlimitedSpeed;

        foreach (ToolStripMenuItem item in _speedMenuItem.DropDownItems)
        {
            var choice = (SpeedChoice)item.Tag!;
            item.Checked = choice.Unlimited == _unlimitedSpeed && Math.Abs(choice.Multiplier - _speedMultiplier) < 0.001;
        }

        if (!_speedMenuItem.DropDownItems.Cast<ToolStripMenuItem>().Any(item => item.Checked))
        {
            ((ToolStripMenuItem)_speedMenuItem.DropDownItems[0]).Checked = true;
            _speedMultiplier = 1.0;
            _unlimitedSpeed = false;
        }
    }

    private void SavePersistedSettings()
    {
        _settings.UseBios = _useBiosMenuItem.Checked;
        _settings.Autosave = _autosaveMenuItem.Checked;
        _settings.BiosPath = _biosPath;
        _settings.SpeedMultiplier = _speedMultiplier;
        _settings.UnlimitedSpeed = _unlimitedSpeed;
        _settings.Save();
    }

    private void RememberRecentRom(string path)
    {
        _settings.RecentRoms.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentRoms.Insert(0, path);
        if (_settings.RecentRoms.Count > MaxRecentRoms)
        {
            _settings.RecentRoms.RemoveRange(MaxRecentRoms, _settings.RecentRoms.Count - MaxRecentRoms);
        }

        SavePersistedSettings();
        RefreshRecentRomsMenu();
    }

    private void RefreshRecentRomsMenu()
    {
        _recentRomsMenuItem.DropDownItems.Clear();
        var existing = _settings.RecentRoms
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxRecentRoms)
            .ToList();
        _settings.RecentRoms.Clear();
        _settings.RecentRoms.AddRange(existing);

        if (existing.Count == 0)
        {
            _recentRomsMenuItem.DropDownItems.Add("(none)").Enabled = false;
        }
        else
        {
            foreach (var romPath in existing)
            {
                _recentRomsMenuItem.DropDownItems.Add(Path.GetFileName(romPath), null, async (_, _) => await OpenRomPathAsync(romPath)).ToolTipText = romPath;
            }
        }

        _recentRomsMenuItem.DropDownItems.Add(new ToolStripSeparator());
        _recentRomsMenuItem.DropDownItems.Add("Clear Recent ROMs", null, (_, _) =>
        {
            _settings.RecentRoms.Clear();
            SavePersistedSettings();
            RefreshRecentRomsMenu();
        }).Enabled = existing.Count > 0;
    }

    private async void OpenStartupRomIfNeeded()
    {
        if (_startupRomPath is null)
        {
            return;
        }

        if (!File.Exists(_startupRomPath))
        {
            MessageBox.Show(this, $"ROM not found: {_startupRomPath}", "Could not open startup ROM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        await OpenRomPathAsync(_startupRomPath);
    }

    private bool HandleShortcut(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.O:
                OpenRom();
                return true;
            case Keys.Control | Keys.B:
                OpenBios();
                return true;
            case Keys.Control | Keys.S:
                WriteSave();
                return true;
            case Keys.Control | Keys.F:
                StepFrame();
                return true;
            case Keys.Space:
                TogglePause();
                return true;
            case Keys.F5:
                ResetRom();
                return true;
            case Keys.F9:
                SaveScreenshot();
                return true;
            default:
                return false;
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (GetDroppedRomPath(e) is null)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        e.Effect = DragDropEffects.Copy;
    }

    private async void OnDragDrop(object? sender, DragEventArgs e)
    {
        var path = GetDroppedRomPath(e);
        if (path is not null)
        {
            await OpenRomPathAsync(path);
        }
    }

    private static string? GetDroppedRomPath(DragEventArgs e)
    {
        if (e.Data is null || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return null;
        }

        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        return files?.FirstOrDefault(file => string.Equals(Path.GetExtension(file), ".gba", StringComparison.OrdinalIgnoreCase));
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

    private readonly record struct SpeedChoice(double Multiplier, bool Unlimited);

    private sealed class DesktopSettings
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public string? BiosPath { get; set; }

        public bool UseBios { get; set; } = true;

        public bool Autosave { get; set; } = true;

        public double SpeedMultiplier { get; set; } = 1.0;

        public bool UnlimitedSpeed { get; set; }

        public List<string> RecentRoms { get; set; } = [];

        public static DesktopSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return new DesktopSettings();
                }

                return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new DesktopSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new DesktopSettings();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static string SettingsPath
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "gbaSharp", "desktop-settings.json");
    }
}
