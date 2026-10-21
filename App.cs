using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Fm4Zip
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            XMem.Init();
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
        }
    }

    class ItemInfo
    {
        public bool IsUp, IsFolder;
        public ZEntry E;
        public string Path;   // full path inside the archive (folders end with "/")
        public string Label;
        public long Size, Comp;
        public int Count;
    }

    class ProgressForm : Form
    {
        ProgressBar bar = new ProgressBar();
        Label lbl = new Label();
        Button cancel = new Button();
        public volatile bool Cancelled;

        public ProgressForm(string title, float s)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size((int)(420 * s), (int)(120 * s));
            lbl.SetBounds((int)(12 * s), (int)(12 * s), (int)(396 * s), (int)(20 * s));
            lbl.AutoEllipsis = true;
            bar.SetBounds((int)(12 * s), (int)(38 * s), (int)(396 * s), (int)(22 * s));
            bar.Maximum = 1000;
            cancel.Text = "Cancel";
            cancel.SetBounds((int)(166 * s), (int)(76 * s), (int)(88 * s), (int)(28 * s));
            cancel.Click += delegate { Cancelled = true; cancel.Enabled = false; lbl.Text = "Cancelling..."; };
            Controls.Add(lbl); Controls.Add(bar); Controls.Add(cancel);
        }

        public void Report(double frac, string text)
        {
            if (!IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (Cancelled) return;
                    bar.Value = Math.Max(0, Math.Min(1000, (int)(frac * 1000)));
                    lbl.Text = text;
                }));
            }
            catch { }
        }
    }

    class InputForm : Form
    {
        TextBox tb = new TextBox();
        public string Value { get { return tb.Text; } }

        public InputForm(string title, string prompt, string initial, float s)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size((int)(380 * s), (int)(110 * s));
            Label l = new Label();
            l.Text = prompt;
            l.SetBounds((int)(12 * s), (int)(12 * s), (int)(356 * s), (int)(20 * s));
            tb.Text = initial;
            tb.SetBounds((int)(12 * s), (int)(36 * s), (int)(356 * s), (int)(24 * s));
            Button ok = new Button(); ok.Text = "OK"; ok.DialogResult = DialogResult.OK;
            ok.SetBounds((int)(200 * s), (int)(72 * s), (int)(80 * s), (int)(28 * s));
            Button ca = new Button(); ca.Text = "Cancel"; ca.DialogResult = DialogResult.Cancel;
            ca.SetBounds((int)(288 * s), (int)(72 * s), (int)(80 * s), (int)(28 * s));
            AcceptButton = ok; CancelButton = ca;
            Controls.Add(l); Controls.Add(tb); Controls.Add(ok); Controls.Add(ca);
            Shown += delegate
            {
                tb.Focus();
                int dot = initial.LastIndexOf('.');
                tb.Select(0, dot > 0 ? dot : initial.Length);
            };
        }
    }

    class ItemSorter : System.Collections.IComparer
    {
        public int Col; public bool Asc = true;
        public int Compare(object a, object b)
        {
            ItemInfo x = (ItemInfo)((ListViewItem)a).Tag, y = (ItemInfo)((ListViewItem)b).Tag;
            if (x.IsUp != y.IsUp) return x.IsUp ? -1 : 1;
            if (x.IsFolder != y.IsFolder) return x.IsFolder ? -1 : 1;
            int r;
            switch (Col)
            {
                case 1: r = x.Size.CompareTo(y.Size); break;
                case 2: r = x.Comp.CompareTo(y.Comp); break;
                case 3: r = string.Compare(Ext(x), Ext(y), StringComparison.CurrentCultureIgnoreCase); break;
                case 4: r = (x.E == null ? DateTime.MinValue : x.E.Modified).CompareTo(y.E == null ? DateTime.MinValue : y.E.Modified); break;
                case 5: r = (x.E == null ? 0u : x.E.Crc).CompareTo(y.E == null ? 0u : y.E.Crc); break;
                case 6: r = (x.E == null ? 0 : x.E.Method).CompareTo(y.E == null ? 0 : y.E.Method); break;
                default: r = 0; break;
            }
            if (r == 0) r = string.Compare(x.Label, y.Label, StringComparison.CurrentCultureIgnoreCase);
            return Asc ? r : -r;
        }
        static string Ext(ItemInfo i) { return System.IO.Path.GetExtension(i.Label ?? ""); }
    }

    class Watched
    {
        public string Temp, ArchivePath, EntryName;
        public DateTime LastWrite;
    }

    class MainForm : Form
    {
        // ---- shell icons ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon; public int iIcon; public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO fi, uint cb, uint flags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        const string AppName = "FM4 ZIP Editor";

        ZArchive arc;
        string curDir = "";
        float S;
        ListView lv;
        ToolStrip ts;
        MenuStrip ms;
        StatusStrip ss;
        ToolStripStatusLabel stLeft, stRight;
        TextBox pathBox;
        Button upBtn;
        Label hint;
        ImageList small;
        ItemSorter sorter = new ItemSorter();
        Dictionary<string, int> iconIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> typeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int addMethod = 21;
        bool draggingOut;
        List<Watched> watched = new List<Watched>();
        System.Windows.Forms.Timer watchTimer = new System.Windows.Forms.Timer();
        bool prompting;
        string tempRoot = Path.Combine(Path.GetTempPath(), "FM4ZipEditor_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        ToolStripMenuItem miXmem, miDefl, miStore;
        ContextMenuStrip ctx;

        ToolStripItem tbOpen, tbAdd, tbExtract, tbTest, tbView, tbRename, tbDelete, tbInfo;
        ToolStripMenuItem mAdd, mAddFolder, mExtract, mTest, mView, mRename, mDelete, mInfo, mClose, mUp;
        ToolStripItem cView, cExtract, cRename, cDelete, cAdd, cAll;

        public MainForm(string openPath)
        {
            using (Graphics g = CreateGraphics()) S = g.DpiX / 96f;
            Font = SystemFonts.MessageBoxFont;
            Text = AppName;
            ClientSize = new Size((int)(960 * S), (int)(560 * S));
            MinimumSize = new Size((int)(640 * S), (int)(360 * S));
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            AllowDrop = true;

            BuildUi();
            Populate();
            UpdateUi();

            watchTimer.Interval = 1000;
            watchTimer.Tick += WatchTick;
            watchTimer.Start();

            Shown += delegate
            {
                if (!XMem.Available)
                    MessageBox.Show(this, "The Forza compression library (xcompress32.dll) cannot be used:\n\n" + XMem.Error +
                        "\n\nYou can open archives with stored or Deflate files, but not XMem ones (most Forza files).",
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (openPath != null && File.Exists(openPath)) OpenArchive(openPath);
            };
            FormClosed += delegate { try { Directory.Delete(tempRoot, true); } catch { } };
        }

        // =============================================================== UI
        static Bitmap Glyph(string glyph, Color c, int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                using (Font f = new Font("Segoe MDL2 Assets", size * 0.72f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush b = new SolidBrush(c))
                {
                    SizeF sz = g.MeasureString(glyph, f);
                    g.DrawString(glyph, f, b, (size - sz.Width) / 2f, (size - sz.Height) / 2f);
                }
            }
            return bmp;
        }

        static bool HasFont(string name)
        {
            foreach (FontFamily f in FontFamily.Families) if (f.Name == name) return true;
            return false;
        }

        ToolStripButton Btn(string text, string glyph, Color col, string tip, EventHandler click)
        {
            ToolStripButton b = new ToolStripButton(text);
            b.TextImageRelation = TextImageRelation.ImageAboveText;
            b.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            b.ToolTipText = tip;
            b.AutoSize = false;
            b.Size = new Size((int)(70 * S), (int)(62 * S));
            if (HasFont("Segoe MDL2 Assets")) b.Image = Glyph(glyph, col, (int)(30 * S));
            b.Click += click;
            return b;
        }

        ToolStripMenuItem Mi(string text, Keys keys, EventHandler click)
        {
            ToolStripMenuItem m = new ToolStripMenuItem(text);
            if (keys != Keys.None) m.ShortcutKeys = keys;
            m.Click += click;
            return m;
        }

        void BuildUi()
        {
            small = new ImageList();
            small.ColorDepth = ColorDepth.Depth32Bit;
            small.ImageSize = new Size((int)(16 * S), (int)(16 * S));

            // ---- liste ----
            lv = new ListView();
            lv.Dock = DockStyle.Fill;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.HideSelection = false;
            lv.MultiSelect = true;
            lv.LabelEdit = false;
            lv.SmallImageList = small;
            lv.ListViewItemSorter = sorter;
            lv.AllowDrop = true;
            lv.Columns.Add("Name", (int)(300 * S));
            lv.Columns.Add("Size", (int)(100 * S), HorizontalAlignment.Right);
            lv.Columns.Add("Packed", (int)(100 * S), HorizontalAlignment.Right);
            lv.Columns.Add("Type", (int)(130 * S));
            lv.Columns.Add("Modified", (int)(130 * S));
            lv.Columns.Add("CRC32", (int)(80 * S));
            lv.Columns.Add("Method", (int)(80 * S));
            typeof(ListView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(lv, true, null);
            lv.ColumnClick += delegate(object o, ColumnClickEventArgs e)
            {
                if (sorter.Col == e.Column) sorter.Asc = !sorter.Asc; else { sorter.Col = e.Column; sorter.Asc = true; }
                lv.Sort();
            };
            lv.ItemActivate += delegate { ActivateSelection(); };
            lv.SelectedIndexChanged += delegate { UpdateUi(); };
            lv.ItemDrag += LvItemDrag;
            lv.DragEnter += OnDragEnter; lv.DragDrop += OnDragDrop;
            lv.KeyDown += delegate(object o, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Back) { GoUp(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.A) { SelectAll(); e.Handled = true; }
            };
            DragEnter += OnDragEnter; DragDrop += OnDragDrop;

            hint = new Label();
            hint.Text = "No archive open.\n\nUse File > Open (Ctrl+O) or drag a .zip file here.";
            hint.TextAlign = ContentAlignment.MiddleCenter;
            hint.ForeColor = SystemColors.GrayText;
            hint.BackColor = SystemColors.Window;
            hint.Dock = DockStyle.Fill;
            hint.AllowDrop = true;
            hint.DragEnter += OnDragEnter; hint.DragDrop += OnDragDrop;
            lv.Controls.Add(hint);

            // ---- barra percorso ----
            Panel pathPanel = new Panel();
            pathPanel.Dock = DockStyle.Top;
            pathPanel.Height = (int)(30 * S);
            pathPanel.Padding = new Padding((int)(4 * S), (int)(3 * S), (int)(4 * S), (int)(3 * S));
            pathBox = new TextBox();
            pathBox.ReadOnly = true;
            pathBox.Dock = DockStyle.Fill;
            pathBox.BackColor = SystemColors.Window;
            upBtn = new Button();
            upBtn.Text = "↑";
            upBtn.Width = (int)(30 * S);
            upBtn.Dock = DockStyle.Left;
            upBtn.FlatStyle = FlatStyle.System;
            upBtn.Click += delegate { GoUp(); };
            pathPanel.Controls.Add(pathBox);
            pathPanel.Controls.Add(upBtn);

            // ---- status ----
            ss = new StatusStrip();
            stLeft = new ToolStripStatusLabel(); stLeft.Spring = true; stLeft.TextAlign = ContentAlignment.MiddleLeft;
            stRight = new ToolStripStatusLabel(); stRight.TextAlign = ContentAlignment.MiddleRight;
            ss.Items.Add(stLeft); ss.Items.Add(stRight);

            // ---- toolbar ----
            ts = new ToolStrip();
            ts.GripStyle = ToolStripGripStyle.Hidden;
            ts.ImageScalingSize = new Size((int)(30 * S), (int)(30 * S));
            ts.BackColor = SystemColors.Control;
            tbOpen = Btn("Open", "", Color.FromArgb(220, 160, 30), "Open archive (Ctrl+O)", delegate { DoOpen(); });
            ToolStripSplitButton add = new ToolStripSplitButton("Add");
            add.TextImageRelation = TextImageRelation.ImageAboveText;
            add.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            add.AutoSize = false; add.Size = new Size((int)(82 * S), (int)(62 * S));
            add.ToolTipText = "Add files to the archive (Ins)";
            if (HasFont("Segoe MDL2 Assets")) add.Image = Glyph("", Color.FromArgb(40, 150, 60), (int)(30 * S));
            add.ButtonClick += delegate { DoAddFiles(); };
            add.DropDownItems.Add("Add files...", null, delegate { DoAddFiles(); });
            add.DropDownItems.Add("Add folder...", null, delegate { DoAddFolder(); });
            tbAdd = add;
            tbExtract = Btn("Extract to", "", Color.FromArgb(30, 110, 200), "Extract to the chosen folder (Ctrl+E)", delegate { DoExtract(); });
            tbTest = Btn("Test", "", Color.FromArgb(40, 150, 60), "Check file integrity (Ctrl+T)", delegate { DoTest(); });
            tbView = Btn("View", "", Color.FromArgb(90, 90, 160), "Open the file with its associated program (Enter)", delegate { ActivateSelection(); });
            tbRename = Btn("Rename", "", Color.FromArgb(110, 110, 110), "Rename (F2)", delegate { DoRename(); });
            tbDelete = Btn("Delete", "", Color.FromArgb(200, 40, 40), "Delete from the archive (Del)", delegate { DoDelete(); });
            tbInfo = Btn("Info", "", Color.FromArgb(30, 110, 200), "Archive information (Ctrl+I)", delegate { DoInfo(); });
            ts.Items.Add(tbOpen); ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(tbAdd); ts.Items.Add(tbExtract); ts.Items.Add(tbTest); ts.Items.Add(tbView);
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(tbRename); ts.Items.Add(tbDelete); ts.Items.Add(new ToolStripSeparator()); ts.Items.Add(tbInfo);

            // ---- menu ----
            ms = new MenuStrip();
            ToolStripMenuItem file = new ToolStripMenuItem("&File");
            mClose = Mi("&Close archive", Keys.None, delegate { CloseArchive(); });
            file.DropDownItems.Add(Mi("&Open archive...", Keys.Control | Keys.O, delegate { DoOpen(); }));
            file.DropDownItems.Add(Mi("&New archive...", Keys.Control | Keys.N, delegate { DoNew(); }));
            file.DropDownItems.Add(mClose);
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Mi("E&xit", Keys.Alt | Keys.F4, delegate { Close(); }));

            ToolStripMenuItem cmd = new ToolStripMenuItem("&Commands");
            mAdd = Mi("Add &files...", Keys.Insert, delegate { DoAddFiles(); });
            mAddFolder = Mi("Add fol&der...", Keys.Control | Keys.Insert, delegate { DoAddFolder(); });
            mExtract = Mi("&Extract to...", Keys.Control | Keys.E, delegate { DoExtract(); });
            mTest = Mi("&Test archive", Keys.Control | Keys.T, delegate { DoTest(); });
            mView = Mi("&View file", Keys.None, delegate { ActivateSelection(); });
            mRename = Mi("&Rename", Keys.F2, delegate { DoRename(); });
            mDelete = Mi("&Delete", Keys.Delete, delegate { DoDelete(); });
            mUp = Mi("&Up one level", Keys.None, delegate { GoUp(); });
            mInfo = Mi("Archive &information", Keys.Control | Keys.I, delegate { DoInfo(); });
            cmd.DropDownItems.AddRange(new ToolStripItem[] { mAdd, mAddFolder, new ToolStripSeparator(), mExtract, mTest, mView,
                new ToolStripSeparator(), mRename, mDelete, new ToolStripSeparator(), mUp, mInfo });

            ToolStripMenuItem opt = new ToolStripMenuItem("&Options");
            ToolStripMenuItem comp = new ToolStripMenuItem("Compression of &added files");
            miXmem = Mi("XMem LZX (Forza format, recommended)", Keys.None, delegate { SetMethod(21); });
            miDefl = Mi("Deflate (standard ZIP)", Keys.None, delegate { SetMethod(8); });
            miStore = Mi("No compression", Keys.None, delegate { SetMethod(0); });
            comp.DropDownItems.AddRange(new ToolStripItem[] { miXmem, miDefl, miStore });
            opt.DropDownItems.Add(comp);
            SetMethod(XMem.Available ? 21 : 8);

            ToolStripMenuItem help = new ToolStripMenuItem("&?");
            help.DropDownItems.Add(Mi("&About " + AppName, Keys.None, delegate
            {
                MessageBox.Show(this, AppName + "\n\nZIP archive editor for Forza Motorsport 4 (XMem LZX compression).\n\n" +
                    "Drag files or folders into the window to add them, or drag files out of the window to extract them.\n" +
                    "With View (double-click) the file is opened: if you edit and save it, the program offers to update the archive.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }));
            ms.Items.AddRange(new ToolStripItem[] { file, cmd, opt, help });
            MainMenuStrip = ms;

            // ---- menu contestuale ----
            ctx = new ContextMenuStrip();
            cView = ctx.Items.Add("Open / View", null, delegate { ActivateSelection(); });
            cExtract = ctx.Items.Add("Extract to...", null, delegate { DoExtract(); });
            ctx.Items.Add(new ToolStripSeparator());
            cRename = ctx.Items.Add("Rename", null, delegate { DoRename(); });
            cDelete = ctx.Items.Add("Delete", null, delegate { DoDelete(); });
            ctx.Items.Add(new ToolStripSeparator());
            cAdd = ctx.Items.Add("Add files...", null, delegate { DoAddFiles(); });
            cAll = ctx.Items.Add("Select all", null, delegate { SelectAll(); });
            ctx.Opening += delegate { UpdateUi(); };
            lv.ContextMenuStrip = ctx;

            Controls.Add(lv);
            Controls.Add(ss);
            Controls.Add(pathPanel);
            Controls.Add(ts);
            Controls.Add(ms);
        }

        void SetMethod(int m)
        {
            addMethod = m;
            miXmem.Checked = m == 21; miDefl.Checked = m == 8; miStore.Checked = m == 0;
        }

        // ============================================================ state / lists
        List<ItemInfo> Selected()
        {
            List<ItemInfo> r = new List<ItemInfo>();
            foreach (ListViewItem i in lv.SelectedItems)
            {
                ItemInfo ii = (ItemInfo)i.Tag;
                if (!ii.IsUp) r.Add(ii);
            }
            return r;
        }

        void UpdateUi()
        {
            bool has = arc != null;
            int sel = Selected().Count;
            foreach (ToolStripItem t in new ToolStripItem[] { tbAdd, mAdd, mAddFolder, cAdd, tbInfo, mInfo, mClose, tbTest, mTest }) t.Enabled = has;
            foreach (ToolStripItem t in new ToolStripItem[] { tbExtract, mExtract, cExtract }) t.Enabled = has && arc.Entries.Count > 0;
            foreach (ToolStripItem t in new ToolStripItem[] { tbView, mView, cView }) t.Enabled = sel == 1;
            foreach (ToolStripItem t in new ToolStripItem[] { tbRename, mRename, cRename }) t.Enabled = sel == 1;
            foreach (ToolStripItem t in new ToolStripItem[] { tbDelete, mDelete, cDelete }) t.Enabled = sel > 0;
            mUp.Enabled = upBtn.Enabled = has && curDir != "";
            cAll.Enabled = has;
            hint.Visible = !has;
            Text = has ? Path.GetFileName(arc.Path) + " - " + AppName : AppName;

            if (!has) { stLeft.Text = ""; stRight.Text = ""; pathBox.Text = ""; return; }
            pathBox.Text = Path.GetFileName(arc.Path) + "\\" + curDir.Replace('/', '\\');
            if (sel > 0)
            {
                long sz = 0; int files = 0;
                foreach (ItemInfo ii in Selected()) { sz += ii.Size; files += ii.IsFolder ? ii.Count : 1; }
                stLeft.Text = "Selected " + sel + " items (" + files + " files), " + sz.ToString("N0") + " bytes";
            }
            else
            {
                long sz = 0; foreach (ZEntry e in arc.Entries) sz += e.UncompSize;
                stLeft.Text = "Total " + arc.Entries.Count + " files, " + sz.ToString("N0") + " bytes";
            }
            stRight.Text = "Compression of added files: " + ZArchive.MethodName(addMethod);
        }

        int IconFor(string name, bool folder)
        {
            string key = folder ? "<dir>" : (Path.GetExtension(name) ?? "").ToLowerInvariant();
            int idx;
            if (iconIdx.TryGetValue(key, out idx)) return idx;
            SHFILEINFO fi = new SHFILEINFO();
            uint attr = folder ? 0x10u : 0x80u;
            string fake = folder ? "folder" : "file" + (key.Length > 0 ? key : ".");
            IntPtr r = SHGetFileInfo(fake, attr, ref fi, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), 0x100 | 0x1 | 0x10 | 0x400);
            idx = 0;
            if (r != IntPtr.Zero && fi.hIcon != IntPtr.Zero)
            {
                using (Icon ic = Icon.FromHandle(fi.hIcon)) small.Images.Add((Icon)ic.Clone());
                DestroyIcon(fi.hIcon);
                idx = small.Images.Count - 1;
            }
            else
            {
                small.Images.Add(SystemIcons.WinLogo);
                idx = small.Images.Count - 1;
            }
            iconIdx[key] = idx;
            typeNames[key] = fi.szTypeName ?? "";
            return idx;
        }

        string TypeOf(string name, bool folder)
        {
            IconFor(name, folder);
            string key = folder ? "<dir>" : (Path.GetExtension(name) ?? "").ToLowerInvariant();
            string t;
            if (typeNames.TryGetValue(key, out t) && t.Length > 0) return t;
            return folder ? "File folder" : "File";
        }

        void Populate()
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            if (arc != null)
            {
                if (curDir != "")
                {
                    ItemInfo up = new ItemInfo(); up.IsUp = true; up.IsFolder = true; up.Label = "..";
                    ListViewItem ui = new ListViewItem("..", IconFor("", true)); ui.Tag = up;
                    lv.Items.Add(ui);
                }
                Dictionary<string, ItemInfo> folders = new Dictionary<string, ItemInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (ZEntry e in arc.Entries)
                {
                    if (!e.Name.StartsWith(curDir, StringComparison.OrdinalIgnoreCase)) continue;
                    string rest = e.Name.Substring(curDir.Length);
                    if (rest.Length == 0) continue;
                    int slash = rest.IndexOf('/');
                    if (slash >= 0)
                    {
                        string fn = rest.Substring(0, slash);
                        ItemInfo fi;
                        if (!folders.TryGetValue(fn, out fi))
                        {
                            fi = new ItemInfo(); fi.IsFolder = true; fi.Label = fn; fi.Path = curDir + fn + "/";
                            folders[fn] = fi;
                        }
                        if (!e.IsDir) { fi.Size += e.UncompSize; fi.Comp += e.CompSize; fi.Count++; }
                    }
                    else
                    {
                        ItemInfo fi = new ItemInfo(); fi.E = e; fi.Label = rest; fi.Path = e.Name;
                        fi.Size = e.UncompSize; fi.Comp = e.CompSize;
                        ListViewItem li = new ListViewItem(rest, IconFor(rest, false));
                        li.SubItems.Add(e.UncompSize.ToString("N0"));
                        li.SubItems.Add(e.CompSize.ToString("N0"));
                        li.SubItems.Add(TypeOf(rest, false));
                        DateTime m = e.Modified;
                        li.SubItems.Add(m == DateTime.MinValue ? "" : m.ToString("g"));
                        li.SubItems.Add(e.Crc.ToString("X8"));
                        li.SubItems.Add(ZArchive.MethodName(e.Method));
                        li.Tag = fi;
                        lv.Items.Add(li);
                    }
                }
                foreach (ItemInfo fi in folders.Values)
                {
                    ListViewItem li = new ListViewItem(fi.Label, IconFor("", true));
                    li.SubItems.Add(fi.Size.ToString("N0"));
                    li.SubItems.Add(fi.Comp.ToString("N0"));
                    li.SubItems.Add(TypeOf("", true));
                    li.SubItems.Add(""); li.SubItems.Add(""); li.SubItems.Add("");
                    li.Tag = fi;
                    lv.Items.Add(li);
                }
                lv.Sort();
            }
            lv.EndUpdate();
        }

        // entries covered by the items (a folder = all of its content)
        List<ZEntry> EntriesOf(List<ItemInfo> items)
        {
            List<ZEntry> r = new List<ZEntry>();
            HashSet<ZEntry> seen = new HashSet<ZEntry>();
            foreach (ItemInfo ii in items)
            {
                foreach (ZEntry e in arc.Entries)
                {
                    bool hit = ii.IsFolder
                        ? e.Name.StartsWith(ii.Path, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(e.Name, ii.Path, StringComparison.OrdinalIgnoreCase);
                    if (hit && seen.Add(e)) r.Add(e);
                }
            }
            return r;
        }

        void SelectAll()
        {
            foreach (ListViewItem i in lv.Items) if (!((ItemInfo)i.Tag).IsUp) i.Selected = true;
        }

        void GoUp()
        {
            if (arc == null || curDir == "") return;
            string t = curDir.TrimEnd('/');
            int k = t.LastIndexOf('/');
            string old = t.Substring(k + 1);
            curDir = k < 0 ? "" : t.Substring(0, k + 1);
            Populate(); UpdateUi();
            foreach (ListViewItem i in lv.Items)
                if (string.Equals(i.Text, old, StringComparison.OrdinalIgnoreCase)) { i.Selected = true; i.Focused = true; i.EnsureVisible(); break; }
        }

        void ActivateSelection()
        {
            if (arc == null) return;
            if (lv.SelectedItems.Count != 1) return;
            ItemInfo ii = (ItemInfo)lv.SelectedItems[0].Tag;
            if (ii.IsUp) { GoUp(); return; }
            if (ii.IsFolder) { curDir = ii.Path; Populate(); UpdateUi(); return; }
            OpenExternal(ii.E);
        }

        // ============================================================ archive
        void DoOpen()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Open archive";
                d.Filter = "ZIP archives (*.zip)|*.zip|All files (*.*)|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) OpenArchive(d.FileName);
            }
        }

        void DoNew()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "New archive";
                d.Filter = "ZIP archives (*.zip)|*.zip";
                d.DefaultExt = "zip";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { ZArchive.CreateEmpty(d.FileName); OpenArchive(d.FileName); }
                catch (Exception ex) { Err(ex); }
            }
        }

        void OpenArchive(string path)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                ZArchive a = ZArchive.Open(path);
                arc = a; curDir = "";
                watched.Clear();
                Populate(); UpdateUi();
            }
            catch (Exception ex) { Err(ex); }
            finally { Cursor = Cursors.Default; }
        }

        void CloseArchive()
        {
            arc = null; curDir = ""; watched.Clear();
            Populate(); UpdateUi();
        }

        void Err(Exception ex)
        {
            MessageBox.Show(this, ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        bool RunBusy(string title, Action<ProgressFn> work)
        {
            ProgressForm f = new ProgressForm(title, S);
            Exception err = null; bool cancelled = false;
            f.Shown += delegate
            {
                Thread th = new Thread(delegate()
                {
                    try { work(delegate(double p, string t) { f.Report(p, t); return !f.Cancelled; }); }
                    catch (OperationCanceledException) { cancelled = true; }
                    catch (Exception ex) { err = ex; }
                    try { f.BeginInvoke(new Action(delegate { f.Close(); })); } catch { }
                });
                th.IsBackground = true;
                th.Start();
            };
            f.ShowDialog(this);
            f.Dispose();
            if (err != null) { Err(err); return false; }
            return !cancelled;
        }

        List<PlanItem> BasePlan(Predicate<ZEntry> drop)
        {
            List<PlanItem> plan = new List<PlanItem>();
            foreach (ZEntry e in arc.Entries)
            {
                if (drop != null && drop(e)) continue;
                PlanItem p = new PlanItem(); p.Src = e; plan.Add(p);
            }
            return plan;
        }

        bool Commit(string title, List<PlanItem> plan)
        {
            bool ok = RunBusy(title, delegate(ProgressFn p) { arc.Rewrite(plan, p); });
            Populate(); UpdateUi();
            return ok;
        }

        // ============================================================ adding files
        void DoAddFiles()
        {
            if (arc == null) return;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Add files to the archive";
                d.Multiselect = true;
                d.Filter = "All files (*.*)|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) AddPaths(d.FileNames);
            }
        }

        void DoAddFolder()
        {
            if (arc == null) return;
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Choose the folder to add to the archive";
                if (d.ShowDialog(this) == DialogResult.OK) AddPaths(new string[] { d.SelectedPath });
            }
        }

        void AddPaths(string[] paths)
        {
            if (arc == null) return;
            List<KeyValuePair<string, string>> add = new List<KeyValuePair<string, string>>(); // archive name, file
            foreach (string p in paths)
            {
                if (Directory.Exists(p))
                {
                    string root = p.TrimEnd('\\', '/');
                    string rootName = Path.GetFileName(root);
                    foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    {
                        string rel = f.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                        add.Add(new KeyValuePair<string, string>(curDir + rootName + "/" + rel, f));
                    }
                }
                else if (File.Exists(p))
                    add.Add(new KeyValuePair<string, string>(curDir + Path.GetFileName(p), p));
            }
            if (add.Count == 0) return;

            Dictionary<string, ZEntry> existing = new Dictionary<string, ZEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ZEntry e in arc.Entries) existing[e.Name] = e;
            int clash = 0;
            foreach (KeyValuePair<string, string> kv in add) if (existing.ContainsKey(kv.Key)) clash++;
            if (clash > 0 && MessageBox.Show(this, clash + " files already exist in the archive.\nDo you want to replace them?", AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            // replaced files keep their position and their original compression method
            Dictionary<string, PlanItem> newItems = new Dictionary<string, PlanItem>(StringComparer.OrdinalIgnoreCase);
            List<PlanItem> appended = new List<PlanItem>();
            foreach (KeyValuePair<string, string> kv in add)
            {
                PlanItem pi = new PlanItem(); pi.Name = kv.Key; pi.FilePath = kv.Value;
                ZEntry old;
                if (existing.TryGetValue(kv.Key, out old))
                {
                    pi.Name = old.Name;
                    pi.Method = old.Method;
                    newItems[old.Name] = pi;
                }
                else { pi.Method = addMethod; appended.Add(pi); }
                if (pi.Method == 21 && !XMem.Available) pi.Method = 8;
            }
            List<PlanItem> plan = new List<PlanItem>();
            foreach (ZEntry e in arc.Entries)
            {
                PlanItem r;
                if (newItems.TryGetValue(e.Name, out r)) plan.Add(r);
                else { PlanItem p = new PlanItem(); p.Src = e; plan.Add(p); }
            }
            plan.AddRange(appended);
            Commit("Adding files...", plan);
        }

        // ============================================================ extraction
        string SafeTarget(string dest, string rel)
        {
            string full = Path.GetFullPath(Path.Combine(dest, rel.Replace('/', '\\')));
            string root = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid path in archive: " + rel);
            return full;
        }

        void ExtractList(List<ZEntry> list, int baseLen, string dest, ProgressFn p)
        {
            long total = 0; foreach (ZEntry e in list) total += Math.Max(1, e.UncompSize);
            long done = 0;
            foreach (ZEntry e in list)
            {
                string rel = e.Name.Substring(Math.Min(baseLen, e.Name.Length));
                if (rel.Length == 0) continue;
                if (p != null && !p(done / (double)total, e.Name)) throw new OperationCanceledException();
                string target = SafeTarget(dest, rel);
                if (e.IsDir) Directory.CreateDirectory(target);
                else arc.Extract(e, target);
                done += Math.Max(1, e.UncompSize);
            }
        }

        void DoExtract()
        {
            if (arc == null) return;
            List<ItemInfo> sel = Selected();
            List<ZEntry> list; int baseLen;
            if (sel.Count > 0) { list = EntriesOf(sel); baseLen = curDir.Length; }
            else { list = new List<ZEntry>(arc.Entries); baseLen = 0; }
            if (list.Count == 0) return;

            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = sel.Count > 0
                    ? "Extract the selected items (" + list.Count + " files) to:"
                    : "Extract the whole archive (" + list.Count + " files) to:";
                d.ShowNewFolderButton = true;
                try { d.SelectedPath = Path.GetDirectoryName(arc.Path); } catch { }
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string dest = d.SelectedPath;

                int exists = 0;
                try
                {
                    foreach (ZEntry e in list)
                        if (!e.IsDir && e.Name.Length > baseLen && File.Exists(SafeTarget(dest, e.Name.Substring(baseLen)))) exists++;
                }
                catch (Exception ex) { Err(ex); return; }
                if (exists > 0 && MessageBox.Show(this, exists + " files already exist in the destination folder.\nDo you want to overwrite them?",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

                if (RunBusy("Extracting...", delegate(ProgressFn p) { ExtractList(list, baseLen, dest, p); }))
                {
                    try { Process.Start("explorer.exe", "\"" + dest + "\""); } catch { }
                }
            }
        }

        void DoTest()
        {
            if (arc == null) return;
            List<ItemInfo> sel = Selected();
            List<ZEntry> list = sel.Count > 0 ? EntriesOf(sel) : new List<ZEntry>(arc.Entries);
            List<string> bad = new List<string>();
            bool ok = RunBusy("Testing...", delegate(ProgressFn p)
            {
                long total = 0; foreach (ZEntry e in list) total += Math.Max(1, e.UncompSize);
                long done = 0;
                foreach (ZEntry e in list)
                {
                    if (!p(done / (double)total, e.Name)) throw new OperationCanceledException();
                    if (e.IsDir) continue;
                    string r = arc.Test(e);
                    if (r != null) bad.Add(e.Name + ": " + r);
                    done += Math.Max(1, e.UncompSize);
                }
            });
            if (!ok) return;
            if (bad.Count == 0)
                MessageBox.Show(this, "Test completed: " + list.Count + " files OK, no errors.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show(this, bad.Count + " files with errors:\n\n" + string.Join("\n", bad.GetRange(0, Math.Min(15, bad.Count)).ToArray()),
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ============================================================ delete / rename / info
        void DoDelete()
        {
            if (arc == null) return;
            List<ItemInfo> sel = Selected();
            if (sel.Count == 0) return;
            List<ZEntry> list = EntriesOf(sel);
            string what = sel.Count == 1 ? "\"" + sel[0].Label + "\"" : sel.Count + " items";
            if (MessageBox.Show(this, "Delete " + what + " (" + list.Count + " files) from the archive?", AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            HashSet<ZEntry> drop = new HashSet<ZEntry>(list);
            Commit("Deleting...", BasePlan(delegate(ZEntry e) { return drop.Contains(e); }));
        }

        void DoRename()
        {
            if (arc == null) return;
            List<ItemInfo> sel = Selected();
            if (sel.Count != 1) return;
            ItemInfo ii = sel[0];
            string newName;
            using (InputForm f = new InputForm("Rename", "New name:", ii.Label, S))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                newName = f.Value.Trim().Replace('\\', '/').Trim('/');
            }
            if (newName.Length == 0 || newName == ii.Label) return;
            if (newName.IndexOfAny(new char[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0)
            {
                MessageBox.Show(this, "The name contains invalid characters.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string oldPath = ii.Path;
            string newPath = curDir + newName + (ii.IsFolder ? "/" : "");

            Dictionary<ZEntry, string> map = new Dictionary<ZEntry, string>();
            foreach (ZEntry e in EntriesOf(sel))
                map[e] = ii.IsFolder ? newPath + e.Name.Substring(oldPath.Length) : newPath;
            HashSet<string> final = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZEntry e in arc.Entries)
            {
                string n; if (!map.TryGetValue(e, out n)) n = e.Name;
                if (!final.Add(n))
                {
                    MessageBox.Show(this, "An item with this name already exists.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            List<PlanItem> plan = BasePlan(null);
            foreach (PlanItem p in plan) { string n; if (map.TryGetValue(p.Src, out n)) p.Name = n; }
            Commit("Renaming...", plan);
        }

        void DoInfo()
        {
            if (arc == null) return;
            long unc = 0, comp = 0; Dictionary<int, int> methods = new Dictionary<int, int>();
            foreach (ZEntry e in arc.Entries)
            {
                unc += e.UncompSize; comp += e.CompSize;
                int c; methods.TryGetValue(e.Method, out c); methods[e.Method] = c + 1;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(arc.Path);
            sb.AppendLine();
            sb.AppendLine("Files: " + arc.Entries.Count);
            sb.AppendLine("Original size: " + unc.ToString("N0") + " bytes");
            sb.AppendLine("Packed size: " + comp.ToString("N0") + " bytes" + (unc > 0 ? "  (" + (100.0 * comp / unc).ToString("0.0") + "%)" : ""));
            sb.AppendLine("Archive size: " + new FileInfo(arc.Path).Length.ToString("N0") + " bytes");
            sb.AppendLine();
            foreach (KeyValuePair<int, int> kv in methods) sb.AppendLine(ZArchive.MethodName(kv.Key) + ": " + kv.Value + " files");
            MessageBox.Show(this, sb.ToString(), "Archive information", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ============================================================ drag & drop
        void OnDragEnter(object sender, DragEventArgs e)
        {
            if (!draggingOut && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            else e.Effect = DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            string[] f = files;
            BeginInvoke(new Action(delegate
            {
                if (arc == null)
                {
                    if (f.Length == 1 && File.Exists(f[0])) OpenArchive(f[0]);
                }
                else AddPaths(f);
            }));
        }

        void LvItemDrag(object sender, ItemDragEventArgs e)
        {
            if (arc == null) return;
            List<ItemInfo> sel = Selected();
            if (sel.Count == 0) return;
            List<ZEntry> list = EntriesOf(sel);
            string dir = Path.Combine(tempRoot, "drag_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try
            {
                Directory.CreateDirectory(dir);
                if (!RunBusy("Preparing files...", delegate(ProgressFn p) { ExtractList(list, curDir.Length, dir, p); })) return;
                List<string> top = new List<string>();
                foreach (string s in Directory.GetFileSystemEntries(dir)) top.Add(s);
                if (top.Count == 0) return;
                draggingOut = true;
                DataObject d = new DataObject(DataFormats.FileDrop, top.ToArray());
                lv.DoDragDrop(d, DragDropEffects.Copy);
            }
            catch (Exception ex) { Err(ex); }
            finally { draggingOut = false; }
        }

        // ============================================================ open with external program + sync
        void OpenExternal(ZEntry e)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                string dir = Path.Combine(tempRoot, Guid.NewGuid().ToString("N").Substring(0, 8));
                string target = Path.Combine(dir, Path.GetFileName(e.Name));
                arc.Extract(e, target);
                Watched w = new Watched();
                w.Temp = target; w.ArchivePath = arc.Path; w.EntryName = e.Name;
                w.LastWrite = File.GetLastWriteTimeUtc(target);
                watched.Add(w);
                Cursor = Cursors.Default;
                try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
                catch (Exception)
                {
                    Process.Start("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + target);
                }
            }
            catch (Exception ex) { Cursor = Cursors.Default; Err(ex); }
        }

        void WatchTick(object sender, EventArgs ev)
        {
            if (prompting || arc == null) return;
            foreach (Watched w in watched.ToArray())
            {
                if (!string.Equals(w.ArchivePath, arc.Path, StringComparison.OrdinalIgnoreCase)) continue;
                DateTime lw;
                try { if (!File.Exists(w.Temp)) continue; lw = File.GetLastWriteTimeUtc(w.Temp); } catch { continue; }
                if (lw == w.LastWrite) continue;
                try { using (File.Open(w.Temp, FileMode.Open, FileAccess.Read, FileShare.Read)) { } }
                catch (IOException) { continue; }   // l'editor sta ancora scrivendo
                w.LastWrite = lw;
                prompting = true;
                try
                {
                    string label = w.EntryName;
                    if (MessageBox.Show(this, "The file \"" + label + "\" has been modified.\nDo you want to update it in the archive?",
                        AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        ZEntry old = arc.Entries.Find(delegate(ZEntry x) { return string.Equals(x.Name, w.EntryName, StringComparison.OrdinalIgnoreCase); });
                        if (old == null) { MessageBox.Show(this, "The file is no longer in the archive.", AppName); }
                        else
                        {
                            List<PlanItem> plan = BasePlan(null);
                            PlanItem pi = new PlanItem(); pi.Name = old.Name; pi.FilePath = w.Temp;
                            pi.Method = (old.Method == 21 && !XMem.Available) ? 8 : old.Method;
                            plan[arc.Entries.IndexOf(old)] = pi;
                            Commit("Updating archive...", plan);
                        }
                    }
                }
                finally { prompting = false; }
                break;
            }
        }
    }
}
