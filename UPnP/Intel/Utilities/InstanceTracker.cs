namespace Intel.Utilities
{
    using System;
    using System.Collections;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Resources;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Windows.Forms;

    public sealed class InstanceTracker : Form
    {
        private ToolBarButton clearEventLogToolBarButton;
        private MenuItem ClearEventMenuItem;
        private MenuItem closeMenuItem;
        private ColumnHeader columnHeader1;
        private ColumnHeader columnHeader2;
        private ColumnHeader columnHeader3;
        private ColumnHeader columnHeader4;
        private IContainer components;
        private static Hashtable DataTable = new Hashtable();
        private ContextMenu DetailMenu;
        private MenuItem DetailMenuItem;
        public static bool Enabled = false;
        private MenuItem enableInstanceTrackingMenuItem;
        private ListView EventListView;
        private TextBox EventText;
        private ToolBar eventToolBar;
        private MenuItem fullGcMenuItem;
        private MenuItem gcMenuItem;
        private ToolBarButton gcToolBarButton;
        private MenuItem haltOnExceptionsMenuItem;
        private ImageList iconImageList;
        private ListView instanceListView;
        private static Hashtable instancetable = new Hashtable();
        private MainMenu mainMenu;
        private MenuItem menuItem1;
        private MenuItem menuItem2;
        private MenuItem menuItem3;
        private MenuItem menuItem4;
        private MenuItem menuItem5;
        private MenuItem menuItem6;
        private MenuItem menuItem8;
        private MenuItem saveAsMenuItem;
        private SaveFileDialog saveLogFileDialog;
        private ToolBarButton showAuditEventsToolBarButton;
        private MenuItem showAuditMenuItem;
        private MenuItem showExceptionsMenuItem;
        private ToolBarButton showExceptionsToolBarButton;
        private ToolBarButton showInformationEventsToolBarButton;
        private MenuItem showInformationMenuItem;
        private ToolBarButton showWarningEventsToolBarButton;
        private MenuItem showWarningsMenuItem;
        private Splitter splitter1;
        private StatusBar statusBar;
        private TabControl tabControl1;
        private TabPage tabPage1;
        private TabPage tabPage2;
        private static long TimeValue;
        private ToolBarButton toolBarButton1;
        private ToolBarButton toolBarButton2;
        private ToolBarButton toolBarButton3;
        private ImageList ToolsIconList;
        private static InstanceTracker tracker = null;
        private static Hashtable WeakTable = new Hashtable();

        public InstanceTracker()
        {
            this.InitializeComponent();
            this.instanceListView.ListViewItemSorter = new InstanceTrackerSorter();
            EventLogger.OnEvent += new EventLogger.EventHandler(this.OnEventSink);
            EventLogger.Enabled = true;
            if (EventLogger.ShowAll)
            {
                this.showInformationMenuItem.Checked = true;
                this.showWarningsMenuItem.Checked = true;
                this.showExceptionsMenuItem.Checked = true;
                this.showAuditMenuItem.Checked = true;
            }
        }

        public static void Add(object o)
        {
            if (Enabled)
            {
                lock (instancetable)
                {
                    StackFrame frame;
                    StackTrace trace = new StackTrace();
                    int index = 0;
                    StringBuilder builder = new StringBuilder();
                    do
                    {
                        frame = trace.GetFrame(index);
                        if (frame != null)
                        {
                            if (builder.Length == 0)
                            {
                                builder.Append(frame.GetMethod().DeclaringType.FullName + " : " + frame.GetMethod().Name);
                            }
                            else
                            {
                                builder.Append("\r\n");
                                builder.Append(frame.GetMethod().DeclaringType.FullName + " : " + frame.GetMethod().Name);
                            }
                        }
                        index++;
                    }
                    while ((frame != null) && (index != 7));
                    string fullName = o.GetType().FullName;
                    if (!DataTable.ContainsKey(fullName))
                    {
                        DataTable[fullName] = new ArrayList();
                    }
                    InstanceStruct struct2 = new InstanceStruct();
                    struct2.WR = new WeakReference(o);
                    struct2.StackList = builder.ToString();
                    ((ArrayList) DataTable[fullName]).Add(struct2);
                    if (tracker != null)
                    {
                        tracker.UpdateDisplayEntry(fullName);
                        tracker.statusBar.BeginInvoke(new TrackerHandler(InstanceTracker.HandleTracker), new object[] { o.GetType().FullName });
                    }
                }
            }
        }

        private void ClearEventMenuItem_Click(object sender, EventArgs e)
        {
            this.EventText.Text = "";
            this.EventListView.Items.Clear();
        }

        private void closeMenuItem_Click(object sender, EventArgs e)
        {
            base.Close();
        }

        private void DetailMenuItem_Click(object sender, EventArgs e)
        {
            ListViewItem item = this.instanceListView.SelectedItems[0];
            string text = item.SubItems[1].Text;
            ArrayList list = (ArrayList) DataTable[text];
            ArrayList dataList = new ArrayList();
            foreach (InstanceStruct struct2 in list)
            {
                if (struct2.WR.IsAlive)
                {
                    dataList.Add(struct2.StackList);
                }
            }
            if (dataList.Count > 0)
            {
                InstanceTracker2 tracker = new InstanceTracker2(dataList);
                tracker.Text = text;
                tracker.ShowDialog();
                tracker.Dispose();
            }
            else
            {
                MessageBox.Show("No details for this item");
            }
        }

        public static void Display()
        {
            if (tracker != null)
            {
                tracker.Activate();
            }
            else
            {
                tracker = new InstanceTracker();
                tracker.Show();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && (this.components != null))
            {
                this.components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void enableInstanceTrackingMenuItem_Click(object sender, EventArgs e)
        {
            this.enableInstanceTrackingMenuItem.Checked = !this.enableInstanceTrackingMenuItem.Checked;
            Enabled = this.enableInstanceTrackingMenuItem.Checked;
        }

        private void EventListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (this.EventListView.SelectedItems.Count > 0)
            {
                string tag = (string) this.EventListView.SelectedItems[0].Tag;
                this.EventText.Text = tag;
            }
        }

        private void eventToolBar_ButtonClick(object sender, ToolBarButtonClickEventArgs e)
        {
            if (e.Button == this.showExceptionsToolBarButton)
            {
                this.showExceptionsMenuItem_Click(this, null);
            }
            if (e.Button == this.showWarningEventsToolBarButton)
            {
                this.showWarningsMenuItem_Click(this, null);
            }
            if (e.Button == this.showInformationEventsToolBarButton)
            {
                this.showInformationMenuItem_Click(this, null);
            }
            if (e.Button == this.showAuditEventsToolBarButton)
            {
                this.showAuditMenuItem_Click(this, null);
            }
            if (e.Button == this.clearEventLogToolBarButton)
            {
                this.ClearEventMenuItem_Click(this, null);
            }
            if (e.Button == this.gcToolBarButton)
            {
                this.gcMenuItem_Click(this, null);
            }
        }

        private void fullGcMenuItem_Click(object sender, EventArgs e)
        {
            this.statusBar.Text = "Memory: " + GC.GetTotalMemory(true).ToString();
        }

        private void gcMenuItem_Click(object sender, EventArgs e)
        {
            GC.Collect();
            this.Recalculate();
            this.UpdateDisplay();
        }

        private void haltOnExceptionsMenuItem_Click(object sender, EventArgs e)
        {
            this.haltOnExceptionsMenuItem.Checked = !this.haltOnExceptionsMenuItem.Checked;
            EventLogger.SetOnExceptionAction(this.haltOnExceptionsMenuItem.Checked);
        }

        public static void HandleTracker(object name)
        {
            tracker.statusBar.Text = "Add: " + ((string) name);
        }

        private void InitializeComponent()
        {
            this.components = new Container();
            ResourceManager manager = new ResourceManager(typeof(InstanceTracker));
            this.DetailMenu = new ContextMenu();
            this.DetailMenuItem = new MenuItem();
            this.statusBar = new StatusBar();
            this.mainMenu = new MainMenu();
            this.menuItem1 = new MenuItem();
            this.saveAsMenuItem = new MenuItem();
            this.menuItem8 = new MenuItem();
            this.gcMenuItem = new MenuItem();
            this.fullGcMenuItem = new MenuItem();
            this.menuItem4 = new MenuItem();
            this.enableInstanceTrackingMenuItem = new MenuItem();
            this.menuItem3 = new MenuItem();
            this.closeMenuItem = new MenuItem();
            this.menuItem2 = new MenuItem();
            this.showExceptionsMenuItem = new MenuItem();
            this.showWarningsMenuItem = new MenuItem();
            this.showInformationMenuItem = new MenuItem();
            this.showAuditMenuItem = new MenuItem();
            this.menuItem5 = new MenuItem();
            this.haltOnExceptionsMenuItem = new MenuItem();
            this.menuItem6 = new MenuItem();
            this.ClearEventMenuItem = new MenuItem();
            this.tabControl1 = new TabControl();
            this.tabPage2 = new TabPage();
            this.EventText = new TextBox();
            this.splitter1 = new Splitter();
            this.EventListView = new ListView();
            this.columnHeader4 = new ColumnHeader();
            this.columnHeader3 = new ColumnHeader();
            this.iconImageList = new ImageList(this.components);
            this.tabPage1 = new TabPage();
            this.instanceListView = new ListView();
            this.columnHeader1 = new ColumnHeader();
            this.columnHeader2 = new ColumnHeader();
            this.ToolsIconList = new ImageList(this.components);
            this.eventToolBar = new ToolBar();
            this.toolBarButton3 = new ToolBarButton();
            this.showExceptionsToolBarButton = new ToolBarButton();
            this.showWarningEventsToolBarButton = new ToolBarButton();
            this.showInformationEventsToolBarButton = new ToolBarButton();
            this.showAuditEventsToolBarButton = new ToolBarButton();
            this.toolBarButton1 = new ToolBarButton();
            this.clearEventLogToolBarButton = new ToolBarButton();
            this.toolBarButton2 = new ToolBarButton();
            this.gcToolBarButton = new ToolBarButton();
            this.saveLogFileDialog = new SaveFileDialog();
            this.tabControl1.SuspendLayout();
            this.tabPage2.SuspendLayout();
            this.tabPage1.SuspendLayout();
            base.SuspendLayout();
            this.DetailMenu.MenuItems.AddRange(new MenuItem[] { this.DetailMenuItem });
            this.DetailMenuItem.Index = 0;
            this.DetailMenuItem.Text = "Details";
            this.DetailMenuItem.Click += new System.EventHandler(this.DetailMenuItem_Click);
            this.statusBar.Location = new Point(0, 0x1b9);
            this.statusBar.Name = "statusBar";
            this.statusBar.Size = new Size(480, 0x10);
            this.statusBar.TabIndex = 1;
            this.mainMenu.MenuItems.AddRange(new MenuItem[] { this.menuItem1, this.menuItem2 });
            this.menuItem1.Index = 0;
            this.menuItem1.MenuItems.AddRange(new MenuItem[] { this.saveAsMenuItem, this.menuItem8, this.gcMenuItem, this.fullGcMenuItem, this.menuItem4, this.enableInstanceTrackingMenuItem, this.menuItem3, this.closeMenuItem });
            this.menuItem1.Text = "&File";
            this.saveAsMenuItem.Index = 0;
            this.saveAsMenuItem.Text = "Save As...";
            this.saveAsMenuItem.Click += new System.EventHandler(this.saveAsMenuItem_Click);
            this.menuItem8.Index = 1;
            this.menuItem8.Text = "-";
            this.gcMenuItem.Index = 2;
            this.gcMenuItem.Text = "&Garbage Collect";
            this.gcMenuItem.Click += new System.EventHandler(this.gcMenuItem_Click);
            this.fullGcMenuItem.Index = 3;
            this.fullGcMenuItem.Text = "&Full Garbage Collect";
            this.fullGcMenuItem.Click += new System.EventHandler(this.fullGcMenuItem_Click);
            this.menuItem4.Index = 4;
            this.menuItem4.Text = "-";
            this.enableInstanceTrackingMenuItem.Index = 5;
            this.enableInstanceTrackingMenuItem.Text = "&Enable Object Tracking";
            this.enableInstanceTrackingMenuItem.Click += new System.EventHandler(this.enableInstanceTrackingMenuItem_Click);
            this.menuItem3.Index = 6;
            this.menuItem3.Text = "-";
            this.closeMenuItem.Index = 7;
            this.closeMenuItem.Text = "&Close";
            this.closeMenuItem.Click += new System.EventHandler(this.closeMenuItem_Click);
            this.menuItem2.Index = 1;
            this.menuItem2.MenuItems.AddRange(new MenuItem[] { this.showExceptionsMenuItem, this.showWarningsMenuItem, this.showInformationMenuItem, this.showAuditMenuItem, this.menuItem5, this.haltOnExceptionsMenuItem, this.menuItem6, this.ClearEventMenuItem });
            this.menuItem2.Text = "Events";
            this.showExceptionsMenuItem.Index = 0;
            this.showExceptionsMenuItem.Text = "Show &Exception Messages";
            this.showExceptionsMenuItem.Click += new System.EventHandler(this.showExceptionsMenuItem_Click);
            this.showWarningsMenuItem.Index = 1;
            this.showWarningsMenuItem.Text = "Show &Warning Messages";
            this.showWarningsMenuItem.Click += new System.EventHandler(this.showWarningsMenuItem_Click);
            this.showInformationMenuItem.Index = 2;
            this.showInformationMenuItem.Text = "Show &Information Messages";
            this.showInformationMenuItem.Click += new System.EventHandler(this.showInformationMenuItem_Click);
            this.showAuditMenuItem.Index = 3;
            this.showAuditMenuItem.Text = "Show &Audit Messages";
            this.showAuditMenuItem.Click += new System.EventHandler(this.showAuditMenuItem_Click);
            this.menuItem5.Index = 4;
            this.menuItem5.Text = "-";
            this.haltOnExceptionsMenuItem.Index = 5;
            this.haltOnExceptionsMenuItem.Text = "&Halt On Exceptions";
            this.haltOnExceptionsMenuItem.Click += new System.EventHandler(this.haltOnExceptionsMenuItem_Click);
            this.menuItem6.Index = 6;
            this.menuItem6.Text = "-";
            this.ClearEventMenuItem.Index = 7;
            this.ClearEventMenuItem.Text = "&Clear Event Log";
            this.ClearEventMenuItem.Click += new System.EventHandler(this.ClearEventMenuItem_Click);
            this.tabControl1.Alignment = TabAlignment.Bottom;
            this.tabControl1.Controls.AddRange(new Control[] { this.tabPage2, this.tabPage1 });
            this.tabControl1.Dock = DockStyle.Fill;
            this.tabControl1.Location = new Point(0, 0x19);
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new Size(480, 0x1a0);
            this.tabControl1.TabIndex = 2;
            this.tabPage2.Controls.AddRange(new Control[] { this.EventText, this.splitter1, this.EventListView });
            this.tabPage2.Location = new Point(4, 4);
            this.tabPage2.Name = "tabPage2";
            this.tabPage2.Size = new Size(0x1d8, 390);
            this.tabPage2.TabIndex = 1;
            this.tabPage2.Text = "Events";
            this.EventText.BorderStyle = BorderStyle.None;
            this.EventText.Dock = DockStyle.Fill;
            this.EventText.Location = new Point(0, 0xb3);
            this.EventText.Multiline = true;
            this.EventText.Name = "EventText";
            this.EventText.ReadOnly = true;
            this.EventText.ScrollBars = ScrollBars.Vertical;
            this.EventText.Size = new Size(0x1d8, 0xd3);
            this.EventText.TabIndex = 1;
            this.EventText.TabStop = false;
            this.EventText.Text = "";
            this.splitter1.BackColor = SystemColors.ControlDark;
            this.splitter1.Dock = DockStyle.Top;
            this.splitter1.Location = new Point(0, 0xb0);
            this.splitter1.Name = "splitter1";
            this.splitter1.Size = new Size(0x1d8, 3);
            this.splitter1.TabIndex = 2;
            this.splitter1.TabStop = false;
            this.EventListView.BorderStyle = BorderStyle.None;
            this.EventListView.Columns.AddRange(new ColumnHeader[] { this.columnHeader4, this.columnHeader3 });
            this.EventListView.Dock = DockStyle.Top;
            this.EventListView.FullRowSelect = true;
            this.EventListView.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            this.EventListView.LargeImageList = this.iconImageList;
            this.EventListView.MultiSelect = false;
            this.EventListView.Name = "EventListView";
            this.EventListView.Size = new Size(0x1d8, 0xb0);
            this.EventListView.SmallImageList = this.iconImageList;
            this.EventListView.TabIndex = 0;
            this.EventListView.View = View.Details;
            this.EventListView.SelectedIndexChanged += new System.EventHandler(this.EventListView_SelectedIndexChanged);
            this.columnHeader4.Text = "Origin";
            this.columnHeader4.Width = 150;
            this.columnHeader3.Text = "Message";
            this.columnHeader3.Width = 300;
            this.iconImageList.ColorDepth = ColorDepth.Depth8Bit;
            this.iconImageList.ImageSize = new Size(0x10, 0x10);
            this.iconImageList.ImageStream = (ImageListStreamer) manager.GetObject("iconImageList.ImageStream");
            this.iconImageList.TransparentColor = Color.Transparent;
            this.tabPage1.Controls.AddRange(new Control[] { this.instanceListView });
            this.tabPage1.Location = new Point(4, 4);
            this.tabPage1.Name = "tabPage1";
            this.tabPage1.Size = new Size(0x1d8, 390);
            this.tabPage1.TabIndex = 0;
            this.tabPage1.Text = "Instances";
            this.instanceListView.BorderStyle = BorderStyle.None;
            this.instanceListView.Columns.AddRange(new ColumnHeader[] { this.columnHeader1, this.columnHeader2 });
            this.instanceListView.ContextMenu = this.DetailMenu;
            this.instanceListView.Dock = DockStyle.Fill;
            this.instanceListView.FullRowSelect = true;
            this.instanceListView.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            this.instanceListView.MultiSelect = false;
            this.instanceListView.Name = "instanceListView";
            this.instanceListView.Size = new Size(0x1d8, 390);
            this.instanceListView.Sorting = SortOrder.Ascending;
            this.instanceListView.TabIndex = 1;
            this.instanceListView.View = View.Details;
            this.columnHeader1.Text = "Count";
            this.columnHeader1.Width = 0x51;
            this.columnHeader2.Text = "Object";
            this.columnHeader2.Width = 370;
            this.ToolsIconList.ColorDepth = ColorDepth.Depth8Bit;
            this.ToolsIconList.ImageSize = new Size(0x10, 0x10);
            this.ToolsIconList.ImageStream = (ImageListStreamer) manager.GetObject("ToolsIconList.ImageStream");
            this.ToolsIconList.TransparentColor = Color.Transparent;
            this.eventToolBar.Appearance = ToolBarAppearance.Flat;
            this.eventToolBar.Buttons.AddRange(new ToolBarButton[] { this.toolBarButton3, this.showExceptionsToolBarButton, this.showWarningEventsToolBarButton, this.showInformationEventsToolBarButton, this.showAuditEventsToolBarButton, this.toolBarButton1, this.clearEventLogToolBarButton, this.toolBarButton2, this.gcToolBarButton });
            this.eventToolBar.DropDownArrows = true;
            this.eventToolBar.ImageList = this.ToolsIconList;
            this.eventToolBar.Name = "eventToolBar";
            this.eventToolBar.ShowToolTips = true;
            this.eventToolBar.Size = new Size(480, 0x19);
            this.eventToolBar.TabIndex = 4;
            this.eventToolBar.ButtonClick += new ToolBarButtonClickEventHandler(this.eventToolBar_ButtonClick);
            this.toolBarButton3.Style = ToolBarButtonStyle.Separator;
            this.showExceptionsToolBarButton.ImageIndex = 3;
            this.showExceptionsToolBarButton.Style = ToolBarButtonStyle.ToggleButton;
            this.showExceptionsToolBarButton.ToolTipText = "Show Exception Messages";
            this.showWarningEventsToolBarButton.ImageIndex = 2;
            this.showWarningEventsToolBarButton.Style = ToolBarButtonStyle.ToggleButton;
            this.showWarningEventsToolBarButton.ToolTipText = "Show Warnings Messages";
            this.showInformationEventsToolBarButton.ImageIndex = 1;
            this.showInformationEventsToolBarButton.Style = ToolBarButtonStyle.ToggleButton;
            this.showInformationEventsToolBarButton.ToolTipText = "Show Information Messages";
            this.showAuditEventsToolBarButton.ImageIndex = 6;
            this.showAuditEventsToolBarButton.Style = ToolBarButtonStyle.ToggleButton;
            this.showAuditEventsToolBarButton.ToolTipText = "Show Audit Messages";
            this.toolBarButton1.Style = ToolBarButtonStyle.Separator;
            this.clearEventLogToolBarButton.ImageIndex = 4;
            this.clearEventLogToolBarButton.ToolTipText = "Clear Event Log";
            this.toolBarButton2.ImageIndex = 5;
            this.toolBarButton2.Style = ToolBarButtonStyle.Separator;
            this.gcToolBarButton.ImageIndex = 5;
            this.gcToolBarButton.ToolTipText = "Force Garbage Collection";
            this.saveLogFileDialog.DefaultExt = "log";
            this.saveLogFileDialog.FileName = "DebugInformation.log";
            this.saveLogFileDialog.Filter = "Log Files|*.log";
            this.saveLogFileDialog.Title = "Save Debug Information Log";
            this.AutoScaleBaseSize = new Size(5, 13);
            base.ClientSize = new Size(480, 0x1c9);
            base.Controls.AddRange(new Control[] { this.tabControl1, this.eventToolBar, this.statusBar });
            base.Icon = (Icon) manager.GetObject("$this.Icon");
            base.Menu = this.mainMenu;
            base.Name = "InstanceTracker";
            base.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Debug Information";
            base.Load += new System.EventHandler(this.InstanceTracker_Load);
            base.Closed += new System.EventHandler(this.InstanceTracker_Closed);
            this.tabControl1.ResumeLayout(false);
            this.tabPage2.ResumeLayout(false);
            this.tabPage1.ResumeLayout(false);
            base.ResumeLayout(false);
        }

        private void InstanceTracker_Closed(object sender, EventArgs e)
        {
            tracker.Dispose();
            tracker = null;
        }

        private void InstanceTracker_Load(object sender, EventArgs e)
        {
            this.UpdateDisplay();
        }

        private void OnEventSink(EventLogEntryType LogType, object origin, string trace, string information)
        {
            base.BeginInvoke(new EventLogger.EventHandler(this.OnEventSinkEx), new object[] { LogType, origin, trace, information });
        }

        private void OnEventSinkEx(EventLogEntryType LogType, object origin, string trace, string information)
        {
            int imageIndex = 0;
            lock (this)
            {
                switch (LogType)
                {
                    case EventLogEntryType.Error:
                        imageIndex = 2;
                        if (this.showExceptionsMenuItem.Checked)
                        {
                            break;
                        }
                        goto Label_0208;

                    case EventLogEntryType.Warning:
                        imageIndex = 1;
                        if (this.showWarningsMenuItem.Checked)
                        {
                            break;
                        }
                        goto Label_0208;

                    case EventLogEntryType.Information:
                        imageIndex = 0;
                        if (this.showInformationMenuItem.Checked)
                        {
                            break;
                        }
                        goto Label_0208;

                    case EventLogEntryType.SuccessAudit:
                        imageIndex = 3;
                        if (this.showAuditMenuItem.Checked)
                        {
                            break;
                        }
                        goto Label_0208;

                    default:
                        imageIndex = 4;
                        break;
                }
                string str = "";
                if (origin.GetType() == typeof(string))
                {
                    str = (string) origin;
                }
                else
                {
                    str = origin.GetType().Name + " [" + origin.GetHashCode().ToString() + "]";
                }
                ListViewItem item = new ListViewItem(new string[] { str, information }, imageIndex);
                if (origin.GetType() == typeof(string))
                {
                    str = (string) origin;
                }
                else
                {
                    str = origin.GetType().FullName + " [" + origin.GetHashCode().ToString() + "]";
                }
                if (trace != "")
                {
                    item.Tag = "Origin: " + str + "\r\nTime: " + DateTime.Now.ToString() + "\r\n\r\n" + information + "\r\n\r\nTRACE:\r\n" + trace;
                }
                else
                {
                    item.Tag = "Origin: " + str + "\r\nTime: " + DateTime.Now.ToString() + "\r\n\r\n" + information;
                }
                this.EventListView.Items.Insert(0, item);
            Label_0208:;
            }
        }

        private void Recalculate()
        {
            lock (instancetable)
            {
                IDictionaryEnumerator enumerator = instancetable.GetEnumerator();
                ArrayList list = new ArrayList();
                while (enumerator.MoveNext())
                {
                    list.Add(enumerator.Key);
                }
                foreach (string str in list)
                {
                    if (!DataTable.ContainsKey(str))
                    {
                        continue;
                    }
                    ArrayList list2 = (ArrayList) DataTable[str];
                    ArrayList list3 = new ArrayList();
                    foreach (InstanceStruct struct2 in list2)
                    {
                        if (!struct2.WR.IsAlive)
                        {
                            list3.Add(struct2);
                        }
                    }
                    foreach (InstanceStruct struct3 in list3)
                    {
                        list2.Remove(struct3);
                    }
                    instancetable[str] = list2.Count;
                }
            }
        }

        private void Recalculate(string name)
        {
            lock (instancetable)
            {
                if (DataTable.ContainsKey(name))
                {
                    ArrayList list = (ArrayList) DataTable[name];
                    ArrayList list2 = new ArrayList();
                    foreach (InstanceStruct struct2 in list)
                    {
                        if (!struct2.WR.IsAlive)
                        {
                            list2.Add(struct2);
                        }
                    }
                    foreach (InstanceStruct struct3 in list2)
                    {
                        list.Remove(struct3);
                    }
                    instancetable[name] = list.Count;
                }
                else
                {
                    instancetable[name] = 0L;
                }
            }
        }

        public static void Remove(object o)
        {
            if (Enabled)
            {
                lock (instancetable)
                {
                    if (tracker != null)
                    {
                        tracker.UpdateDisplayEntry(o.GetType().FullName);
                        tracker.statusBar.Text = "Remove: " + o.GetType().FullName;
                    }
                }
            }
        }

        private void saveAsMenuItem_Click(object sender, EventArgs e)
        {
            if (this.saveLogFileDialog.ShowDialog(this) == DialogResult.OK)
            {
                StreamWriter writer = File.CreateText(this.saveLogFileDialog.FileName);
                foreach (ListViewItem item in this.EventListView.Items)
                {
                    writer.Write(item.Tag.ToString());
                    writer.Write("\r\n\r\n-------------------------------------------------\r\n");
                }
                writer.Close();
            }
        }

        private void showAuditMenuItem_Click(object sender, EventArgs e)
        {
            this.showAuditMenuItem.Checked = !this.showAuditMenuItem.Checked;
            this.showAuditEventsToolBarButton.Pushed = this.showAuditMenuItem.Checked;
            EventLogger.ShowAll = ((this.showAuditMenuItem.Checked || this.showInformationMenuItem.Checked) || this.showExceptionsMenuItem.Checked) || this.showExceptionsMenuItem.Checked;
        }

        private void showExceptionsMenuItem_Click(object sender, EventArgs e)
        {
            this.showExceptionsMenuItem.Checked = !this.showExceptionsMenuItem.Checked;
            this.showExceptionsToolBarButton.Pushed = this.showExceptionsMenuItem.Checked;
            EventLogger.ShowAll = ((this.showAuditMenuItem.Checked || this.showInformationMenuItem.Checked) || this.showExceptionsMenuItem.Checked) || this.showExceptionsMenuItem.Checked;
        }

        private void showInformationMenuItem_Click(object sender, EventArgs e)
        {
            this.showInformationMenuItem.Checked = !this.showInformationMenuItem.Checked;
            this.showInformationEventsToolBarButton.Pushed = this.showInformationMenuItem.Checked;
            EventLogger.ShowAll = ((this.showAuditMenuItem.Checked || this.showInformationMenuItem.Checked) || this.showExceptionsMenuItem.Checked) || this.showExceptionsMenuItem.Checked;
        }

        private void showWarningsMenuItem_Click(object sender, EventArgs e)
        {
            this.showWarningsMenuItem.Checked = !this.showWarningsMenuItem.Checked;
            this.showWarningEventsToolBarButton.Pushed = this.showWarningsMenuItem.Checked;
            EventLogger.ShowAll = ((this.showAuditMenuItem.Checked || this.showInformationMenuItem.Checked) || this.showExceptionsMenuItem.Checked) || this.showExceptionsMenuItem.Checked;
        }

        public static void StartTimer()
        {
            TimeValue = DateTime.Now.Ticks;
        }

        public static void StopTimer()
        {
            StopTimer("");
        }

        public static void StopTimer(string s)
        {
            TimeSpan span = new TimeSpan(DateTime.Now.Ticks - TimeValue);
            MessageBox.Show("Time to execute: " + s + " = " + span.TotalMilliseconds.ToString());
        }

        private void UpdateDisplay()
        {
            lock (instancetable)
            {
                this.instanceListView.Items.Clear();
                foreach (string str in instancetable.Keys)
                {
                    this.instanceListView.Items.Add(new ListViewItem(new string[] { instancetable[str].ToString(), str }));
                }
            }
        }

        private void UpdateDisplayEntry(string name)
        {
            this.Recalculate(name);
            lock (instancetable)
            {
                foreach (ListViewItem item in this.instanceListView.Items)
                {
                    if (item.SubItems[1].Text == name)
                    {
                        item.SubItems[0].Text = instancetable[name].ToString();
                        goto Label_00CB;
                    }
                }
                this.instanceListView.Items.Add(new ListViewItem(new string[] { instancetable[name].ToString(), name }));
            Label_00CB:;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct InstanceStruct
        {
            public WeakReference WR;
            public string StackList;
        }

        private class InstanceTrackerSorter : IComparer
        {
            public int Compare(object x, object y)
            {
                ListViewItem item = (ListViewItem) x;
                ListViewItem item2 = (ListViewItem) y;
                return item.SubItems[1].Text.CompareTo(item2.SubItems[1].Text);
            }
        }

        public delegate void TrackerHandler(object obj);
    }
}

