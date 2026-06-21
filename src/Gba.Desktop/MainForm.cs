using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net.Sockets;
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
    private const int TilePixels = 16;
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
    private readonly ToolStripMenuItem _controlServerMenuItem = new("Local Control Server") { CheckOnClick = true };
    private readonly ToolStripStatusLabel _status = new("No ROM loaded");
    private readonly System.Windows.Forms.Timer _presentTimer = new();
    private readonly System.Windows.Forms.Timer _autosaveTimer = new();
    private readonly WaveOutAudioOutput _audioOutput = new();
    private readonly int[] _argbFrame = new int[VideoController.Pixels];
    private readonly string? _startupRomPath;
    private readonly DesktopStartupOptions _startupOptions;
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
    private DesktopControlServer? _controlServer;

    public MainForm(string? startupRomPath = null)
        : this(new DesktopStartupOptions(startupRomPath))
    {
    }

    internal MainForm(DesktopStartupOptions startupOptions)
    {
        _startupOptions = startupOptions;
        _startupRomPath = string.IsNullOrWhiteSpace(startupOptions.StartupRomPath) ? null : startupOptions.StartupRomPath;
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

        var tools = new ToolStripMenuItem("Tools");
        _controlServerMenuItem.CheckedChanged += OnControlServerMenuItemCheckedChanged;
        tools.DropDownItems.Add(_controlServerMenuItem);
        menu.Items.Add(tools);

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
        UpdateControlServerMenuItem();
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        Shown += (_, _) => StartControlServerIfNeeded();
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
        _controlServer?.Dispose();
        _controlServer = null;
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

    internal DesktopControlStatus GetControlStatus()
    {
        lock (_sync)
        {
            return new DesktopControlStatus(
                _gba is not null,
                _runCancellation is not null,
                _romPath,
                _romPath is null ? null : Path.GetFileName(_romPath),
                Interlocked.Read(ref _emulatedFrames),
                Interlocked.Read(ref _framesPresented),
                (_gba?.Keypad.PressedKeys ?? GbaKey.None).ToString(),
                _speedMultiplier,
                _unlimitedSpeed);
        }
    }

    internal DesktopRubyState GetRubyState()
    {
        lock (_sync)
        {
            return DesktopRubyStateProbe.Capture(_gba, Interlocked.Read(ref _emulatedFrames));
        }
    }

    internal byte[] CaptureControlScreenshotPng(DesktopScreenshotOptions? options = null)
    {
        options ??= new DesktopScreenshotOptions();
        var snapshotPixels = new int[VideoController.Pixels];
        DesktopRubyState? rubyState;
        lock (_sync)
        {
            if (_gba is null)
            {
                throw new InvalidOperationException("No ROM is loaded.");
            }

            Array.Copy(_argbFrame, snapshotPixels, snapshotPixels.Length);
            rubyState = DesktopRubyStateProbe.Capture(_gba, Interlocked.Read(ref _emulatedFrames));
        }

        using var snapshot = new Bitmap(VideoController.Width, VideoController.Height, PixelFormat.Format32bppArgb);
        var data = snapshot.LockBits(
            new Rectangle(0, 0, VideoController.Width, VideoController.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(snapshotPixels, 0, data.Scan0, snapshotPixels.Length);
        }
        finally
        {
            snapshot.UnlockBits(data);
        }

        Bitmap output = snapshot;
        Bitmap? lens = null;
        if (options.IsLens)
        {
            lens = CreateCenterLens(snapshot, options);
            output = lens;
        }

        if (options.HasMovementGrid)
        {
            DrawMovementGrid(output, options.IsLens ? options.Scale : 1, options.HasDenseCoordinates);
        }

        if (options.HasAtlas)
        {
            DrawTileAtlas(output, options.IsLens ? options.Scale : 1, DesktopTileAtlasEntry.Load(options.AtlasPath), rubyState);
        }

        using (var stream = new MemoryStream())
        {
            output.Save(stream, ImageFormat.Png);
            lens?.Dispose();
            return stream.ToArray();
        }
    }

    private static Bitmap CreateCenterLens(Bitmap source, DesktopScreenshotOptions options)
    {
        var maxTiles = Math.Min(13, Math.Min(VideoController.Width, VideoController.Height) / TilePixels);
        if (maxTiles % 2 == 0)
        {
            maxTiles--;
        }

        var tiles = Math.Clamp(options.LensTiles, 3, maxTiles);
        if (tiles % 2 == 0)
        {
            tiles++;
        }

        var size = tiles * TilePixels;
        var left = Math.Clamp((VideoController.Width / 2) - (size / 2), 0, VideoController.Width - size);
        var top = Math.Clamp((VideoController.Height / 2) - (size / 2), 0, VideoController.Height - size);
        var scale = Math.Clamp(options.Scale, 1, 8);
        var lens = new Bitmap(size * scale, size * scale, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(lens);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, lens.Width, lens.Height),
            new Rectangle(left, top, size, size),
            GraphicsUnit.Pixel);
        return lens;
    }

    private static void DrawMovementGrid(Bitmap bitmap, int scale, bool denseCoordinates)
    {
        var centerX = bitmap.Width / 2;
        var centerY = bitmap.Height / 2;
        var tileSize = TilePixels * scale;
        var tileLeft = centerX - (tileSize / 2);
        var tileTop = centerY - (tileSize / 2);
        using var graphics = Graphics.FromImage(bitmap);
        using var gridPen = new Pen(Color.FromArgb(140, 255, 255, 255), Math.Max(1, scale / 2));
        using var centerPen = new Pen(Color.FromArgb(230, 255, 235, 59), Math.Max(1, scale));
        using var adjacentPen = new Pen(Color.FromArgb(210, 0, 229, 255), Math.Max(1, scale));
        using var coordinatePen = new Pen(Color.FromArgb(150, 255, 255, 255), Math.Max(1, scale / 2));
        using var crossPen = new Pen(Color.FromArgb(220, 255, 64, 129), Math.Max(1, scale));
        using var font = new Font(SystemFonts.DefaultFont.FontFamily, Math.Max(8, 8 * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        using var coordinateFont = new Font(SystemFonts.DefaultFont.FontFamily, Math.Max(7, 5 * scale), FontStyle.Regular, GraphicsUnit.Pixel);
        using var labelBrush = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        using var shadowBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0));

        for (var x = PositiveMod(tileLeft, tileSize) - tileSize; x <= bitmap.Width; x += tileSize)
        {
            graphics.DrawLine(gridPen, x, 0, x, bitmap.Height);
        }

        for (var y = PositiveMod(tileTop, tileSize) - tileSize; y <= bitmap.Height; y += tileSize)
        {
            graphics.DrawLine(gridPen, 0, y, bitmap.Width, y);
        }

        DrawTile(graphics, adjacentPen, tileLeft, tileTop - tileSize, tileSize);
        DrawTile(graphics, adjacentPen, tileLeft + tileSize, tileTop, tileSize);
        DrawTile(graphics, adjacentPen, tileLeft, tileTop + tileSize, tileSize);
        DrawTile(graphics, adjacentPen, tileLeft - tileSize, tileTop, tileSize);
        DrawTile(graphics, centerPen, tileLeft, tileTop, tileSize);
        if (scale > 1)
        {
            DrawCoordinateLabels(graphics, bitmap.Width, bitmap.Height, tileLeft, tileTop, tileSize, coordinateFont, labelBrush, shadowBrush, denseCoordinates);
            DrawExtendedTileMarkers(graphics, coordinatePen, tileLeft, tileTop, tileSize);
        }

        graphics.DrawLine(crossPen, centerX - tileSize, centerY, centerX + tileSize, centerY);
        graphics.DrawLine(crossPen, centerX, centerY - tileSize, centerX, centerY + tileSize);

        DrawLabel(graphics, "C", tileLeft, tileTop, tileSize, font, labelBrush, shadowBrush);
        DrawLabel(graphics, "U", tileLeft, tileTop - tileSize, tileSize, font, labelBrush, shadowBrush);
        DrawLabel(graphics, "R", tileLeft + tileSize, tileTop, tileSize, font, labelBrush, shadowBrush);
        DrawLabel(graphics, "D", tileLeft, tileTop + tileSize, tileSize, font, labelBrush, shadowBrush);
        DrawLabel(graphics, "L", tileLeft - tileSize, tileTop, tileSize, font, labelBrush, shadowBrush);
    }

    private static void DrawTileAtlas(Bitmap bitmap, int scale, IReadOnlyList<DesktopTileAtlasEntry> entries, DesktopRubyState? rubyState)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var centerX = bitmap.Width / 2;
        var centerY = bitmap.Height / 2;
        var tileSize = TilePixels * scale;
        var tileLeft = centerX - (tileSize / 2);
        var tileTop = centerY - (tileSize / 2);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font(SystemFonts.DefaultFont.FontFamily, Math.Max(7, 5 * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        using var actionFont = new Font(SystemFonts.DefaultFont.FontFamily, Math.Max(7, 4 * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        using var labelBrush = new SolidBrush(Color.FromArgb(245, 255, 255, 255));
        using var shadowBrush = new SolidBrush(Color.FromArgb(220, 0, 0, 0));
        using var actionBrush = new SolidBrush(Color.FromArgb(245, 232, 255, 232));
        using var actionFillBrush = new SolidBrush(Color.FromArgb(50, 0, 200, 83));
        using var actionBorderPen = new Pen(Color.FromArgb(245, 0, 200, 83), Math.Max(2, scale));

        foreach (var entry in entries)
        {
            if (!TryGetAtlasRelativeTile(entry, rubyState, out var dx, out var dy))
            {
                continue;
            }

            var color = GetAtlasColor(entry.Type);
            using var fillBrush = new SolidBrush(Color.FromArgb(72, color));
            using var borderPen = new Pen(Color.FromArgb(235, color), Math.Max(1, scale));
            var x = tileLeft + dx * tileSize;
            var y = tileTop + dy * tileSize;
            var width = entry.Width * tileSize;
            var height = entry.Height * tileSize;
            if (x + width < 0 || y + height < 0 || x > bitmap.Width || y > bitmap.Height)
            {
                continue;
            }

            graphics.FillRectangle(fillBrush, x, y, width, height);
            graphics.DrawRectangle(borderPen, x, y, width, height);
            var label = GetCompactAtlasLabel(entry);
            if (!string.IsNullOrWhiteSpace(label) && scale > 1)
            {
                DrawSmallLabel(graphics, label, x + 3, y + 3, font, labelBrush, shadowBrush);
            }

            if (TryGetAtlasRelativeStandTile(entry, rubyState, out var standDx, out var standDy))
            {
                var standX = tileLeft + standDx * tileSize;
                var standY = tileTop + standDy * tileSize;
                if (standX + tileSize >= 0 && standY + tileSize >= 0 && standX <= bitmap.Width && standY <= bitmap.Height)
                {
                    graphics.FillRectangle(actionFillBrush, standX, standY, tileSize, tileSize);
                    graphics.DrawRectangle(actionBorderPen, standX, standY, tileSize, tileSize);
                    var action = string.IsNullOrWhiteSpace(entry.Action) ? "stand" : entry.Action;
                    if (scale > 1)
                    {
                        DrawSmallLabel(graphics, action, standX + 3, standY + tileSize - (5 * scale) - 4, actionFont, actionBrush, shadowBrush);
                    }
                }
            }
        }
    }

    private static string GetCompactAtlasLabel(DesktopTileAtlasEntry entry)
    {
        var label = string.IsNullOrWhiteSpace(entry.Label) ? entry.Type : entry.Label;
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        var lastDash = label.LastIndexOf('-');
        return lastDash >= 0 && lastDash + 1 < label.Length ? label[(lastDash + 1)..] : label;
    }

    private static bool TryGetAtlasRelativeTile(DesktopTileAtlasEntry entry, DesktopRubyState? rubyState, out int dx, out int dy)
    {
        dx = entry.Dx;
        dy = entry.Dy;
        if (entry.X is not { } absoluteX || entry.Y is not { } absoluteY)
        {
            return true;
        }

        if (rubyState?.SaveBlockPlayer is not { } player)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(entry.MapId)
            && !entry.MapId.Equals(player.MapId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        dx = absoluteX - player.X;
        dy = absoluteY - player.Y;
        return true;
    }

    private static bool TryGetAtlasRelativeStandTile(DesktopTileAtlasEntry entry, DesktopRubyState? rubyState, out int dx, out int dy)
    {
        dx = 0;
        dy = 0;
        if (entry.StandX is not { } standX || entry.StandY is not { } standY)
        {
            return false;
        }

        if (rubyState?.SaveBlockPlayer is not { } player)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(entry.MapId)
            && !entry.MapId.Equals(player.MapId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        dx = standX - player.X;
        dy = standY - player.Y;
        return true;
    }

    private static Color GetAtlasColor(string type)
    {
        return type.Trim().ToLowerInvariant() switch
        {
            "blocker" or "wall" or "tree" or "water" or "counter" => Color.FromArgb(244, 67, 54),
            "door" or "warp" or "stairs" => Color.FromArgb(33, 150, 243),
            "interactable" or "sign" or "npc" or "object" => Color.FromArgb(255, 152, 0),
            "ledge" => Color.FromArgb(156, 39, 176),
            "grass" or "passable" or "path" => Color.FromArgb(76, 175, 80),
            _ => Color.FromArgb(255, 235, 59)
        };
    }

    private static void DrawExtendedTileMarkers(Graphics graphics, Pen pen, int centerLeft, int centerTop, int size)
    {
        for (var distance = 2; distance <= 4; distance++)
        {
            DrawTile(graphics, pen, centerLeft, centerTop - size * distance, size);
            DrawTile(graphics, pen, centerLeft + size * distance, centerTop, size);
            DrawTile(graphics, pen, centerLeft, centerTop + size * distance, size);
            DrawTile(graphics, pen, centerLeft - size * distance, centerTop, size);
        }
    }

    private static void DrawCoordinateLabels(Graphics graphics, int width, int height, int centerLeft, int centerTop, int size, Font font, Brush brush, Brush shadow, bool dense)
    {
        var minDx = (int)Math.Floor((0 - centerLeft) / (double)size);
        var maxDx = (int)Math.Ceiling((width - centerLeft) / (double)size) - 1;
        var minDy = (int)Math.Floor((0 - centerTop) / (double)size);
        var maxDy = (int)Math.Ceiling((height - centerTop) / (double)size) - 1;

        for (var dy = minDy; dy <= maxDy; dy++)
        {
            for (var dx = minDx; dx <= maxDx; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                if (!dense && dx != 0 && dy != 0)
                {
                    continue;
                }

                var x = centerLeft + dx * size;
                var y = centerTop + dy * size;
                if (x + size < 0 || y + size < 0 || x > width || y > height)
                {
                    continue;
                }

                DrawSmallLabel(graphics, FormatCoordinate(dx, dy), x + 2, y + 2, font, brush, shadow);
            }
        }
    }

    private static string FormatCoordinate(int dx, int dy)
        => $"{FormatSigned(dx)},{FormatSigned(dy)}";

    private static string FormatSigned(int value)
        => value > 0 ? $"+{value}" : value.ToString();

    private static void DrawTile(Graphics graphics, Pen pen, int x, int y, int size)
    {
        graphics.DrawRectangle(pen, x, y, size, size);
    }

    private static void DrawLabel(Graphics graphics, string text, int x, int y, int size, Font font, Brush brush, Brush shadow)
    {
        var point = new PointF(x + size / 2f - font.Size / 3f, y + size / 2f - font.Size / 2f);
        graphics.DrawString(text, font, shadow, point.X + 1, point.Y + 1);
        graphics.DrawString(text, font, brush, point);
    }

    private static void DrawSmallLabel(Graphics graphics, string text, int x, int y, Font font, Brush brush, Brush shadow)
    {
        graphics.DrawString(text, font, shadow, x + 1, y + 1);
        graphics.DrawString(text, font, brush, x, y);
    }

    private static int PositiveMod(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    internal void ControlPress(GbaKey keys)
    {
        lock (_sync)
        {
            _gba?.Keypad.Press(keys);
        }
    }

    internal void ControlRelease(GbaKey keys)
    {
        lock (_sync)
        {
            _gba?.Keypad.Release(keys);
        }
    }

    internal void ControlSetKeys(GbaKey keys)
    {
        lock (_sync)
        {
            _gba?.Keypad.SetPressedKeys(keys);
        }
    }

    internal Task ControlRunAsync() => InvokeOnUiThreadAsync(StartEmulation);

    internal Task ControlPauseAsync() => InvokeOnUiThreadAsync(PauseEmulation);

    internal Task ControlTogglePauseAsync() => InvokeOnUiThreadAsync(TogglePause);

    internal Task ControlResetAsync() => InvokeOnUiThreadAsync(ResetRom);

    internal Task ControlStepFrameAsync() => InvokeOnUiThreadAsync(StepFrame);

    internal Task ControlCloseAsync() => InvokeOnUiThreadAsync(Close);

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
        UpdateControlServerMenuItem();
    }

    private void UpdateControlServerMenuItem()
    {
        _controlServerMenuItem.CheckedChanged -= OnControlServerMenuItemCheckedChanged;
        _controlServerMenuItem.Checked = _controlServer is not null;
        _controlServerMenuItem.CheckedChanged += OnControlServerMenuItemCheckedChanged;
        _controlServerMenuItem.Text = _controlServer is null
            ? "Local Control Server"
            : $"Local Control Server ({_controlServer.BaseUrl})";
        _controlServerMenuItem.ToolTipText = _controlServer is null
            ? "Start the localhost automation server for screenshots and live input."
            : "Stop the localhost automation server.";
    }

    private void OnControlServerMenuItemCheckedChanged(object? sender, EventArgs e) => ToggleControlServer();

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

    private void StartControlServerIfNeeded()
    {
        if (!_startupOptions.ControlServerEnabled || _controlServer is not null)
        {
            return;
        }

        StartControlServerFromUi();
    }

    private void ToggleControlServer()
    {
        if (_controlServerMenuItem.Checked)
        {
            StartControlServerFromUi();
        }
        else
        {
            StopControlServerFromUi();
        }
    }

    private void StartControlServerFromUi()
    {
        if (_controlServer is not null)
        {
            UpdateControlServerMenuItem();
            return;
        }

        try
        {
            _controlServer = DesktopControlServer.Start(this, _startupOptions.ControlPort);
            SetStatus($"Control server started: {_controlServer.BaseUrl}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException)
        {
            SetStatus($"Control server unavailable: {ex.Message}");
        }
        finally
        {
            UpdateControlServerMenuItem();
        }
    }

    private void StopControlServerFromUi()
    {
        if (_controlServer is null)
        {
            UpdateControlServerMenuItem();
            return;
        }

        _controlServer.Dispose();
        _controlServer = null;
        SetStatus("Control server stopped.");
        UpdateControlServerMenuItem();
    }

    private Task InvokeOnUiThreadAsync(Action action)
    {
        if (IsDisposed)
        {
            throw new InvalidOperationException("The desktop window has been disposed.");
        }

        if (!InvokeRequired)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke((MethodInvoker)(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }));

        return completion.Task;
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
