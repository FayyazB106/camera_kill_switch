// CameraSwitch.exe -- company camera master switch (Windows)
//
// A tray app + test window that turns the camera fully OFF (blocked for every
// app) and back ON. It disables the physical camera *device*, so it stops Zoom,
// Teams, browsers -- everything -- not just Windows Store apps.
//
// Self-provisioning: on the first toggle the app registers two elevated scheduled
// tasks ("CameraKillSwitch-Disable" / "CameraKillSwitch-Enable") via the Task
// Scheduler COM API (one UAC prompt). They are pre-authorized so a standard user
// can TRIGGER them afterward with NO admin prompt -- that is what makes the switch
// freely user-toggleable. Once the tasks exist, every toggle just runs them.
//
// Build:  windows\app\Build-App.ps1   (uses the built-in .NET Framework csc.exe)

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CameraKillSwitch
{
    static class Program
    {
        // Single-instance coordination. Names include the app GUID so they don't
        // collide with anything else on the machine. Per-user (Local\) scope:
        // one instance per interactive session, which is what a tray app wants.
        internal const string MutexName     = @"Local\CameraKillSwitch_SingleInstance_9C4B2E7A";
        internal const string ShowEventName = @"Local\CameraKillSwitch_ShowWindow_9C4B2E7A";

        static Mutex _mutex;

        [STAThread]
        static void Main()
        {
            bool createdNew;
            _mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                // Another instance already owns the switch. Ask it to surface its
                // window (so the user sees *something* happen), then exit quietly.
                Log.W("Another instance is running; signalling it and exiting.");
                try
                {
                    using (var ev = EventWaitHandle.OpenExisting(ShowEventName))
                        ev.Set();
                }
                catch { /* the other instance may not be ready yet; just exit */ }
                return;
            }

            Log.W("=== Camera Kill Switch start (admin=" + Camera.IsAdmin() + ") ===");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var app = new SwitchApp())
                Application.Run(app);

            GC.KeepAlive(_mutex);   // keep the mutex alive for the whole run
        }
    }

    enum CamState { On, Off, Mixed, None, Unknown }
    enum ArtStyle { Enabled, Disabled, Waiting }

    // Lightweight diagnostic log, OFF by default. Enable for troubleshooting by
    // setting the environment variable CAMKILL_DEBUG=1 before launching; the log
    // is then written to logs.txt next to the exe.
    static class Log
    {
        static readonly bool Enabled =
            Environment.GetEnvironmentVariable("CAMKILL_DEBUG") == "1";
        static readonly string Path =
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs.txt");
        static readonly object gate = new object();

        public static void W(string msg)
        {
            if (!Enabled) return;
            try
            {
                lock (gate)
                    File.AppendAllText(Path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { /* logging must never break the app */ }
        }
    }

    // Runs the camera queries/actions via PowerShell's PnP cmdlets, so we don't
    // need heavy SetupAPI P/Invoke. Camera class only -- never touches the
    // "Image" class (scanners/printers live there on modern Windows).
    static class Camera
    {
        const string GetCams =
            "$c = Get-PnpDevice -Class Camera -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.PNPClass -eq 'Camera' };";

        static string Run(string psScript, out int exitCode)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" +
                            psScript.Replace("\"", "\\\"") + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                exitCode = p.ExitCode;
                return outp.Trim();
            }
        }

        public static CamState GetState()
        {
            int rc;
            string script = GetCams +
                "$ok = @($c | Where-Object Status -eq 'OK').Count;" +
                "$err = @($c | Where-Object Status -eq 'Error').Count;" +
                "if ($ok -eq 0 -and $err -eq 0) { 'None' }" +
                "elseif ($ok -gt 0 -and $err -eq 0) { 'On' }" +
                "elseif ($ok -eq 0 -and $err -gt 0) { 'Off' }" +
                "else { 'Mixed' }";
            string s = Run(script, out rc);
            Log.W("GetState rc=" + rc + " raw='" + s + "'");
            switch (s)
            {
                case "On": return CamState.On;
                case "Off": return CamState.Off;
                case "Mixed": return CamState.Mixed;
                case "None": return CamState.None;
                default: return CamState.Unknown;
            }
        }

        // Whether the pre-authorized elevated tasks exist (installed mode).
        public static bool TasksInstalled()
        {
            int rc;
            Run("$null = Get-ScheduledTask -TaskName 'CameraKillSwitch-Disable' -ErrorAction Stop", out rc);
            return rc == 0;
        }

        // Turn the camera on/off. Returns true if the task actually fired.
        //
        // Fast path: just try to run the task -- if it's set up and we have run
        // rights, it works with no prompt. If that fails for ANY reason, (re)register
        // once via UAC and retry. We do NOT gate on being able to *see* the task,
        // because a standard user often can't enumerate a SYSTEM task even though
        // they're allowed to run it.
        public static bool Set(bool on)
        {
            string task = on ? "CameraKillSwitch-Enable" : "CameraKillSwitch-Disable";
            Log.W("Set(on=" + on + ") task=" + task);

            if (RunSchtasks(task)) { Log.W("Set: fast-path run succeeded"); return true; }

            Log.W("Set: fast-path failed, running one-time elevated setup");
            bool ensured = EnsureTasks();
            Log.W("Set: EnsureTasks returned " + ensured);
            if (ensured && RunSchtasks(task)) { Log.W("Set: run after setup succeeded"); return true; }

            Log.W("Set: FAILED");
            return false;
        }

        // Trigger a pre-registered elevated task. No window, no prompt.
        // Returns true only if schtasks reports success (exit code 0).
        static bool RunSchtasks(string task)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/run /tn \"" + task + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd().Trim();
                string e = p.StandardError.ReadToEnd().Trim();
                p.WaitForExit();
                Log.W("RunSchtasks('" + task + "') exit=" + p.ExitCode +
                      (o.Length > 0 ? " out='" + o + "'" : "") +
                      (e.Length > 0 ? " err='" + e + "'" : ""));
                return p.ExitCode == 0;
            }
        }

        // One-time setup: elevate once and register the two SYSTEM tasks plus a
        // tiny engine script they call. Registers via the Task Scheduler COM API
        // with an explicit security descriptor (SDDL) that grants Authenticated
        // Users read+execute -- WITHOUT that, a standard/non-elevated user gets
        // "Access is denied" when triggering the task. That was the bug.
        static bool EnsureTasks()
        {
            string flag    = Path.Combine(Path.GetTempPath(), "CameraKillSwitchSetup.ok");
            string setupLog = Path.Combine(Path.GetTempPath(), "CameraKillSwitchSetup.log");

            // The elevated script writes its own transcript to $setupLog and drops
            // an .ok marker on success -- that's how we know it worked, since we
            // can't read the elevated process's stdout across the UAC boundary.
            string setupScript =
@"$ErrorActionPreference = 'Stop'
$log  = Join-Path $env:TEMP 'CameraKillSwitchSetup.log'
$flag = Join-Path $env:TEMP 'CameraKillSwitchSetup.ok'
Remove-Item $flag -ErrorAction SilentlyContinue
""[$(Get-Date -Format o)] setup start"" | Out-File $log
try {
    $dir = Join-Path $env:ProgramData 'CameraKillSwitch'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $engine = Join-Path $dir 'cam.ps1'
@'
param([ValidateSet(""Disable"",""Enable"")]$Action)
$c = Get-PnpDevice -Class Camera -ErrorAction SilentlyContinue |
    Where-Object { $_.PNPClass -eq ""Camera"" -and $_.Status -in ""OK"",""Error"" }
if ($Action -eq ""Disable"") { $c | Disable-PnpDevice -Confirm:$false -ErrorAction SilentlyContinue }
else                         { $c | Enable-PnpDevice  -Confirm:$false -ErrorAction SilentlyContinue }
'@ | Set-Content -Path $engine -Encoding UTF8
    ""engine written to $engine"" | Add-Content $log

    $svc = New-Object -ComObject Schedule.Service
    $svc.Connect()
    $root = $svc.GetFolder('\')
    # Remove tasks/files from the app's previous name (CameraSwitch), if present.
    foreach ($old in 'CameraSwitch-Disable','CameraSwitch-Enable') { try { $root.DeleteTask($old, 0); ""removed legacy $old"" | Add-Content $log } catch {} }
    Remove-Item (Join-Path $env:ProgramData 'CameraSwitch') -Recurse -Force -ErrorAction SilentlyContinue
    # Owner/group Admins; Admins+SYSTEM full; Users & Authenticated Users read+execute (run).
    $sddl = 'O:BAG:BAD:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;BU)(A;;FRFX;;;AU)'
    foreach ($a in 'Disable','Enable') {
        try { $root.DeleteTask('CameraKillSwitch-' + $a, 0) } catch {}   # clear any broken/locked task first
        $def = $svc.NewTask(0)
        $def.RegistrationInfo.Description = 'Company Camera Kill Switch'
        $def.Principal.UserId = 'S-1-5-18'
        $def.Principal.LogonType = 5
        $def.Principal.RunLevel = 1
        $def.Settings.Enabled = $true
        $def.Settings.ExecutionTimeLimit = 'PT2M'
        $def.Settings.MultipleInstances = 2
        $def.Settings.DisallowStartIfOnBatteries = $false
        $def.Settings.StopIfGoingOnBatteries = $false
        $act = $def.Actions.Create(0)
        $act.Path = 'powershell.exe'
        $act.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""' + $engine + '"" -Action ' + $a
        $root.RegisterTaskDefinition('CameraKillSwitch-' + $a, $def, 6, 'S-1-5-18', $null, 5, $sddl) | Out-Null
        ""registered CameraKillSwitch-$a"" | Add-Content $log
    }
    'ok' | Set-Content $flag
    ""[$(Get-Date -Format o)] setup ok"" | Add-Content $log
} catch {
    ""ERROR: $($_.Exception.Message)"" | Add-Content $log
    ""$($_.ScriptStackTrace)"" | Add-Content $log
    exit 1
}";

            try
            {
                try { File.Delete(flag); } catch { }
                string tmp = Path.Combine(Path.GetTempPath(), "CameraKillSwitchSetup.ps1");
                File.WriteAllText(tmp, setupScript);

                Log.W("EnsureTasks: launching elevated setup (expect one UAC prompt)");
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + tmp + "\"",
                    UseShellExecute = true,
                    Verb = "runas",          // single UAC prompt
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi)) { p.WaitForExit(); Log.W("EnsureTasks: elevated exit=" + p.ExitCode); }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.W("EnsureTasks: elevation cancelled/failed: " + ex.Message);
                return false;
            }

            if (File.Exists(setupLog))
            {
                try { Log.W("---- setup.log ----\r\n" + File.ReadAllText(setupLog).Trim() + "\r\n---- end setup.log ----"); }
                catch { }
            }
            bool ok = File.Exists(flag);
            Log.W("EnsureTasks: success marker present=" + ok);
            return ok;
        }

        public static bool IsAdmin()
        {
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var pr = new System.Security.Principal.WindowsPrincipal(id);
                return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
        }
    }

    // Renders the tray/window graphics from the company camera artwork.
    // The camera picture is the shared PNG from Camera.svg / No-camera.svg; the
    // "disabled" look adds the red circle + slash overlay exactly as No-camera.svg
    // (circle r=45 @ centre 50,50, diagonal slash, #FF0000, stroke width 10 on a
    // 100x100 canvas). Drawing the overlay in code keeps it crisp at any size.
    static class CamArt
    {
        const string CameraPngBase64 =
            "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";

        static Image _camera;
        static Image Camera
        {
            get
            {
                if (_camera == null)
                {
                    byte[] bytes = Convert.FromBase64String(CameraPngBase64);
                    _camera = Image.FromStream(new MemoryStream(bytes));
                }
                return _camera;
            }
        }

        // Render the camera artwork at the given square size:
        //   Enabled  -> plain camera            (Camera.svg)
        //   Disabled -> red circle + slash       (No-camera.svg)
        //   Waiting  -> yellow circle, no slash   (Waiting camera.svg)
        public static Bitmap Render(int size, ArtStyle style)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);
                g.DrawImage(Camera, 0, 0, size, size);

                if (style != ArtStyle.Enabled)
                {
                    float f = size / 100f;   // SVG coordinates are on a 100x100 canvas
                    Color ring = style == ArtStyle.Disabled
                        ? Color.FromArgb(255, 0, 0)     // red   (#FF0000)
                        : Color.FromArgb(255, 255, 0);  // yellow (#FFFF00)
                    using (var pen = new Pen(ring, 10f * f))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        g.DrawEllipse(pen, 5f * f, 5f * f, 90f * f, 90f * f);          // r=45 @ (50,50)
                        if (style == ArtStyle.Disabled)
                            g.DrawLine(pen, 17.46f * f, 85.46f * f, 85.46f * f, 17.46f * f); // slash
                    }
                }
            }
            return bmp;
        }

        public static Icon Icon(int size, ArtStyle style)
        {
            using (var bmp = Render(size, style))
                return System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }
    }

    class SwitchApp : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly StatusForm form;
        readonly Icon iconEnabled  = CamArt.Icon(32, ArtStyle.Enabled);
        readonly Icon iconDisabled = CamArt.Icon(32, ArtStyle.Disabled);
        readonly Icon iconWaiting  = CamArt.Icon(32, ArtStyle.Waiting);
        readonly EventWaitHandle showEvent;
        CamState state = CamState.Unknown;
        bool busy;

        public SwitchApp()
        {
            // Listen for a second launch asking us to surface the window.
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            var listener = new Thread(() =>
            {
                while (true)
                {
                    try { showEvent.WaitOne(); }
                    catch { break; }
                    try { form.BeginInvoke(new Action(ShowForm)); } catch { }
                }
            }) { IsBackground = true, Name = "ShowWindowListener" };
            listener.Start();

            tray = new NotifyIcon { Visible = true };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Camera Kill Switch", null, (s, e) => ShowForm());
            menu.Items.Add("Toggle camera", null, (s, e) => Toggle());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitThread());
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Toggle(); };
            tray.DoubleClick += (s, e) => ShowForm();

            form = new StatusForm(Toggle);
            Refresh();
            ShowForm(); // show once on launch so testing is obvious
        }

        void ShowForm()
        {
            form.Show();
            form.WindowState = FormWindowState.Normal;
            form.BringToFront();
            form.Activate();
        }

        public void Toggle()
        {
            if (busy) return;   // ignore clicks while a transition is in flight
            busy = true;

            bool turnOn = (state == CamState.Off);      // default to turning OFF unless already off
            CamState target = turnOn ? CamState.On : CamState.Off;

            // Show the busy state immediately: full yellow banner + Enabling/Disabling.
            tray.Icon = iconWaiting;
            tray.Text = turnOn ? "Camera: enabling..." : "Camera: disabling...";
            form.SetBusy(turnOn ? "Enabling..." : "Disabling...", Color.FromArgb(240, 190, 0));

            // Do the actual work off the UI thread so the window paints the busy
            // state and stays responsive; marshal the result back when done.
            var worker = new Thread(() =>
            {
                Camera.Set(turnOn);   // fire the action; judge success by real state, not exit code

                // The device takes a moment to change. Poll its actual state --
                // exit codes lie (ghost devices, async tasks); hardware state does not.
                bool reached = false;
                for (int i = 0; i < 12; i++)      // up to ~4.8s
                {
                    Thread.Sleep(400);
                    state = Camera.GetState();
                    if (state == target) { reached = true; break; }
                }
                Log.W("Toggle: target=" + target + " reached=" + reached + " finalState=" + state);

                try
                {
                    form.BeginInvoke(new Action(() =>
                    {
                        Refresh();
                        busy = false;
                        if (reached)
                            tray.ShowBalloonTip(2000, "Camera Kill Switch",
                                target == CamState.Off ? "Camera is now DISABLED" : "Camera is now ENABLED",
                                ToolTipIcon.Info);
                        else
                            tray.ShowBalloonTip(2500, "Camera Kill Switch",
                                "Could not change the camera. If a UAC prompt appeared, it may have been cancelled.",
                                ToolTipIcon.Warning);
                    }));
                }
                catch { busy = false; }
            }) { IsBackground = true, Name = "CameraToggle" };
            worker.Start();
        }

        void Refresh()
        {
            state = Camera.GetState();
            bool disabled = (state != CamState.On);   // only the confirmed-enabled state shows the plain camera
            Color c; string text;
            switch (state)
            {
                case CamState.On:   c = Color.FromArgb(46,204,113);  text = "Camera: ENABLED";  break;
                case CamState.Off:  c = Color.FromArgb(231,76,60);   text = "Camera: DISABLED"; break;
                case CamState.None: c = Color.FromArgb(149,165,166); text = "No camera detected"; break;
                default:            c = Color.FromArgb(243,156,18);  text = "Camera: " + state; break;
            }
            tray.Icon = disabled ? iconDisabled : iconEnabled;
            tray.Text = text + "  (click to toggle)";
            form.SetState(state, text, c);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tray.Visible = false; tray.Dispose(); form.Dispose();
                iconEnabled.Dispose(); iconDisabled.Dispose(); iconWaiting.Dispose();
                if (showEvent != null) showEvent.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // A simple window: status banner, usage instructions, and one toggle button.
    class StatusForm : Form
    {
        readonly Panel banner = new Panel();
        readonly Label status = new Label();
        readonly Label instructions = new Label();
        readonly Button toggleBtn = new Button();
        readonly Action toggle;

        public StatusForm(Action toggleAction)
        {
            toggle = toggleAction;

            Text = "Camera Kill Switch";
            Icon = CamArt.Icon(32, ArtStyle.Enabled);
            ClientSize = new Size(400, 290);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;

            // Status banner across the top: colored background, centered text.
            banner.Dock = DockStyle.Top; banner.Height = 72;
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleCenter;
            status.Font = new Font("Segoe UI", 16, FontStyle.Bold);
            status.ForeColor = Color.White;
            banner.Controls.Add(status);

            // Usage instructions. Left-aligned, wraps naturally.
            instructions.SetBounds(24, banner.Height + 16, ClientSize.Width - 48, 108);
            instructions.TextAlign = ContentAlignment.TopLeft;
            instructions.Font = new Font("Segoe UI", 9.75f, FontStyle.Regular);
            instructions.ForeColor = Color.FromArgb(70, 70, 70);
            instructions.Text =
                "Turn the camera on or off with the button below, or by " +
                "left-clicking the Camera Kill Switch icon in the system tray " +
                "(bottom-right of the taskbar, near the clock).\r\n\r\n" +
                "Right-click the tray icon for more options.";

            toggleBtn.Text = "Toggle Camera";
            toggleBtn.Font = new Font("Segoe UI", 12, FontStyle.Regular);
            toggleBtn.Size = new Size(200, 46);
            toggleBtn.Location = new Point((ClientSize.Width - 200) / 2, ClientSize.Height - 64);
            toggleBtn.Click += (s, e) => toggle();

            Controls.Add(toggleBtn);
            Controls.Add(instructions);
            Controls.Add(banner);

            // Closing hides to tray instead of exiting.
            FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            };
        }

        public void SetState(CamState st, string text, Color color)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetState(st, text, color))); return; }
            banner.BackColor = color;
            status.Text = text;
            toggleBtn.Enabled = (st != CamState.None);
            toggleBtn.Text = st == CamState.Off ? "Enable Camera" : "Disable Camera";
        }

        public void SetBusy(string msg, Color color)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetBusy(msg, color))); return; }
            banner.BackColor = color;
            status.Text = msg;
            toggleBtn.Enabled = false;   // no double-clicks mid-transition
        }
    }
}
