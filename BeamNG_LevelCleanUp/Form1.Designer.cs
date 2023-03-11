namespace BeamNG_LevelCleanUp
{
    partial class Form1
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            tabControl1 = new TabControl();
            tabPage1 = new TabPage();
            splitContainerShrink = new SplitContainer();
            btnZipDeployment = new Button();
            labelFileSummaryShrink = new Label();
            btnOpenLog = new Button();
            tbBeamLogPath = new TextBox();
            label2 = new Label();
            btn_deleteFiles = new Button();
            chkDryRun = new CheckBox();
            btn_AnalyzeLevel = new Button();
            splitContainer_grid = new SplitContainer();
            cbAllNoneDeleteList = new CheckBox();
            tbFilterGridDeleteList = new TextBox();
            dataGridViewDeleteList = new DataGridView();
            tabPage3 = new TabPage();
            btnZipDeployment2 = new Button();
            label6 = new Label();
            tb_rename_new_name_title = new TextBox();
            Btn_RenameLevel = new Button();
            BtnGetCurrentName = new Button();
            label5 = new Label();
            tb_rename_new_name_path = new TextBox();
            label4 = new Label();
            tb_rename_current_name = new TextBox();
            tabPage2 = new TabPage();
            richTextBoxErrors = new RichTextBox();
            tabPage4 = new TabPage();
            splitContainerCopyAssets = new SplitContainer();
            labelFileSummaryCopy = new Label();
            BtnCopyAssets = new Button();
            label8 = new Label();
            BtnScanAssets = new Button();
            BtnCopyFromZipLevel = new Button();
            lbLevelNameCopyFrom = new Label();
            tbCopyFromLevel = new TextBox();
            label9 = new Label();
            dataGridViewCopyList = new DataGridView();
            cbAllNoneCopyList = new CheckBox();
            tbFilterGridCopyList = new TextBox();
            tabPage5 = new TabPage();
            blazorWebView1 = new Microsoft.AspNetCore.Components.WebView.WindowsForms.BlazorWebView();
            coboCompressionLevel1 = new ComboBox();
            tbProgress = new TextBox();
            btnLoadLevelZipFile = new Button();
            tbLevelZipFile = new TextBox();
            label3 = new Label();
            btn_openLevelFolder = new Button();
            tbLevelPath = new TextBox();
            label1 = new Label();
            folderBrowserDialogLevel = new FolderBrowserDialog();
            openFileDialogLog = new OpenFileDialog();
            openFileDialogZip = new OpenFileDialog();
            panel1 = new Panel();
            label7 = new Label();
            backgroundWorker1 = new System.ComponentModel.BackgroundWorker();
            tabControl1.SuspendLayout();
            tabPage1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)splitContainerShrink).BeginInit();
            splitContainerShrink.Panel1.SuspendLayout();
            splitContainerShrink.Panel2.SuspendLayout();
            splitContainerShrink.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)splitContainer_grid).BeginInit();
            splitContainer_grid.Panel1.SuspendLayout();
            splitContainer_grid.Panel2.SuspendLayout();
            splitContainer_grid.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)dataGridViewDeleteList).BeginInit();
            tabPage3.SuspendLayout();
            tabPage2.SuspendLayout();
            tabPage4.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)splitContainerCopyAssets).BeginInit();
            splitContainerCopyAssets.Panel1.SuspendLayout();
            splitContainerCopyAssets.Panel2.SuspendLayout();
            splitContainerCopyAssets.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)dataGridViewCopyList).BeginInit();
            tabPage5.SuspendLayout();
            panel1.SuspendLayout();
            SuspendLayout();
            // 
            // tabControl1
            // 
            tabControl1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            tabControl1.Controls.Add(tabPage1);
            tabControl1.Controls.Add(tabPage3);
            tabControl1.Controls.Add(tabPage2);
            tabControl1.Controls.Add(tabPage4);
            tabControl1.Controls.Add(tabPage5);
            tabControl1.Location = new Point(0, 140);
            tabControl1.Name = "tabControl1";
            tabControl1.SelectedIndex = 0;
            tabControl1.Size = new Size(1327, 763);
            tabControl1.TabIndex = 0;
            // 
            // tabPage1
            // 
            tabPage1.Controls.Add(splitContainerShrink);
            tabPage1.Location = new Point(4, 29);
            tabPage1.Name = "tabPage1";
            tabPage1.Padding = new Padding(3);
            tabPage1.Size = new Size(1319, 730);
            tabPage1.TabIndex = 0;
            tabPage1.Text = "Shrink Deploymentfile";
            tabPage1.UseVisualStyleBackColor = true;
            // 
            // splitContainerShrink
            // 
            splitContainerShrink.Dock = DockStyle.Fill;
            splitContainerShrink.FixedPanel = FixedPanel.Panel1;
            splitContainerShrink.Location = new Point(3, 3);
            splitContainerShrink.Name = "splitContainerShrink";
            splitContainerShrink.Orientation = Orientation.Horizontal;
            // 
            // splitContainerShrink.Panel1
            // 
            splitContainerShrink.Panel1.Controls.Add(btnZipDeployment);
            splitContainerShrink.Panel1.Controls.Add(labelFileSummaryShrink);
            splitContainerShrink.Panel1.Controls.Add(btnOpenLog);
            splitContainerShrink.Panel1.Controls.Add(tbBeamLogPath);
            splitContainerShrink.Panel1.Controls.Add(label2);
            splitContainerShrink.Panel1.Controls.Add(btn_deleteFiles);
            splitContainerShrink.Panel1.Controls.Add(chkDryRun);
            splitContainerShrink.Panel1.Controls.Add(btn_AnalyzeLevel);
            splitContainerShrink.Panel1.Padding = new Padding(10);
            splitContainerShrink.Panel1MinSize = 100;
            // 
            // splitContainerShrink.Panel2
            // 
            splitContainerShrink.Panel2.AutoScroll = true;
            splitContainerShrink.Panel2.Controls.Add(splitContainer_grid);
            splitContainerShrink.Panel2.Padding = new Padding(10);
            splitContainerShrink.Panel2MinSize = 300;
            splitContainerShrink.Size = new Size(1313, 724);
            splitContainerShrink.SplitterDistance = 170;
            splitContainerShrink.TabIndex = 14;
            // 
            // btnZipDeployment
            // 
            btnZipDeployment.Location = new Point(342, 125);
            btnZipDeployment.Name = "btnZipDeployment";
            btnZipDeployment.Size = new Size(146, 29);
            btnZipDeployment.TabIndex = 29;
            btnZipDeployment.Text = "3. Zip Deployment";
            btnZipDeployment.TextImageRelation = TextImageRelation.TextAboveImage;
            btnZipDeployment.UseVisualStyleBackColor = true;
            btnZipDeployment.Click += btnZipDeployment_Click;
            // 
            // labelFileSummaryShrink
            // 
            labelFileSummaryShrink.AutoSize = true;
            labelFileSummaryShrink.Dock = DockStyle.Right;
            labelFileSummaryShrink.Location = new Point(1253, 10);
            labelFileSummaryShrink.Name = "labelFileSummaryShrink";
            labelFileSummaryShrink.Size = new Size(50, 20);
            labelFileSummaryShrink.TabIndex = 24;
            labelFileSummaryShrink.Text = "label2";
            // 
            // btnOpenLog
            // 
            btnOpenLog.Location = new Point(578, 48);
            btnOpenLog.Name = "btnOpenLog";
            btnOpenLog.Size = new Size(178, 29);
            btnOpenLog.TabIndex = 22;
            btnOpenLog.Text = "Select BeamNg.log";
            btnOpenLog.UseVisualStyleBackColor = true;
            btnOpenLog.Click += btnOpenLog_Click;
            // 
            // tbBeamLogPath
            // 
            tbBeamLogPath.Location = new Point(8, 50);
            tbBeamLogPath.Name = "tbBeamLogPath";
            tbBeamLogPath.Size = new Size(564, 27);
            tbBeamLogPath.TabIndex = 21;
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(18, 27);
            label2.Name = "label2";
            label2.Size = new Size(295, 20);
            label2.TabIndex = 20;
            label2.Text = "BeamNg.log for excluding Errors in 2nd run";
            // 
            // btn_deleteFiles
            // 
            btn_deleteFiles.Location = new Point(185, 125);
            btn_deleteFiles.Name = "btn_deleteFiles";
            btn_deleteFiles.Size = new Size(117, 29);
            btn_deleteFiles.TabIndex = 18;
            btn_deleteFiles.Text = "2. Delete Files";
            btn_deleteFiles.TextImageRelation = TextImageRelation.TextAboveImage;
            btn_deleteFiles.UseVisualStyleBackColor = true;
            btn_deleteFiles.Click += btn_deleteFiles_Click;
            // 
            // chkDryRun
            // 
            chkDryRun.AutoSize = true;
            chkDryRun.Checked = true;
            chkDryRun.CheckState = CheckState.Checked;
            chkDryRun.Location = new Point(10, 83);
            chkDryRun.Name = "chkDryRun";
            chkDryRun.Size = new Size(198, 24);
            chkDryRun.TabIndex = 17;
            chkDryRun.Text = "Dry Run without Deletion";
            chkDryRun.UseVisualStyleBackColor = true;
            // 
            // btn_AnalyzeLevel
            // 
            btn_AnalyzeLevel.Location = new Point(7, 125);
            btn_AnalyzeLevel.Name = "btn_AnalyzeLevel";
            btn_AnalyzeLevel.Size = new Size(142, 29);
            btn_AnalyzeLevel.TabIndex = 16;
            btn_AnalyzeLevel.Text = "1. Analyze Level";
            btn_AnalyzeLevel.UseVisualStyleBackColor = true;
            btn_AnalyzeLevel.Click += btn_AnalyzeLevel_Click;
            // 
            // splitContainer_grid
            // 
            splitContainer_grid.Dock = DockStyle.Fill;
            splitContainer_grid.FixedPanel = FixedPanel.Panel1;
            splitContainer_grid.Location = new Point(10, 10);
            splitContainer_grid.Name = "splitContainer_grid";
            splitContainer_grid.Orientation = Orientation.Horizontal;
            // 
            // splitContainer_grid.Panel1
            // 
            splitContainer_grid.Panel1.Controls.Add(cbAllNoneDeleteList);
            splitContainer_grid.Panel1.Controls.Add(tbFilterGridDeleteList);
            splitContainer_grid.Panel1MinSize = 50;
            // 
            // splitContainer_grid.Panel2
            // 
            splitContainer_grid.Panel2.Controls.Add(dataGridViewDeleteList);
            splitContainer_grid.Panel2MinSize = 300;
            splitContainer_grid.Size = new Size(1293, 530);
            splitContainer_grid.SplitterDistance = 51;
            splitContainer_grid.TabIndex = 25;
            // 
            // cbAllNoneDeleteList
            // 
            cbAllNoneDeleteList.Checked = true;
            cbAllNoneDeleteList.CheckState = CheckState.Checked;
            cbAllNoneDeleteList.Dock = DockStyle.Top;
            cbAllNoneDeleteList.Location = new Point(0, 27);
            cbAllNoneDeleteList.Name = "cbAllNoneDeleteList";
            cbAllNoneDeleteList.Size = new Size(1293, 24);
            cbAllNoneDeleteList.TabIndex = 23;
            cbAllNoneDeleteList.Text = "Selection All / None";
            cbAllNoneDeleteList.UseVisualStyleBackColor = true;
            cbAllNoneDeleteList.CheckedChanged += cbAllNoneDeleteList_CheckedChanged;
            // 
            // tbFilterGridDeleteList
            // 
            tbFilterGridDeleteList.Dock = DockStyle.Top;
            tbFilterGridDeleteList.Location = new Point(0, 0);
            tbFilterGridDeleteList.Name = "tbFilterGridDeleteList";
            tbFilterGridDeleteList.PlaceholderText = "Search...";
            tbFilterGridDeleteList.Size = new Size(1293, 27);
            tbFilterGridDeleteList.TabIndex = 24;
            tbFilterGridDeleteList.TextChanged += tbFilterGridDeleteList_TextChanged;
            // 
            // dataGridViewDeleteList
            // 
            dataGridViewDeleteList.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            dataGridViewDeleteList.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            dataGridViewDeleteList.Dock = DockStyle.Fill;
            dataGridViewDeleteList.Location = new Point(0, 0);
            dataGridViewDeleteList.Name = "dataGridViewDeleteList";
            dataGridViewDeleteList.RowHeadersWidth = 51;
            dataGridViewDeleteList.RowTemplate.Height = 29;
            dataGridViewDeleteList.Size = new Size(1293, 475);
            dataGridViewDeleteList.TabIndex = 0;
            dataGridViewDeleteList.CellContentClick += dataGridViewDeleteList_CellContentClick;
            dataGridViewDeleteList.CellValueChanged += dataGridViewDeleteList_CellValueChanged;
            dataGridViewDeleteList.CurrentCellDirtyStateChanged += dataGridViewDeleteList_CurrentCellDirtyStateChanged;
            // 
            // tabPage3
            // 
            tabPage3.Controls.Add(btnZipDeployment2);
            tabPage3.Controls.Add(label6);
            tabPage3.Controls.Add(tb_rename_new_name_title);
            tabPage3.Controls.Add(Btn_RenameLevel);
            tabPage3.Controls.Add(BtnGetCurrentName);
            tabPage3.Controls.Add(label5);
            tabPage3.Controls.Add(tb_rename_new_name_path);
            tabPage3.Controls.Add(label4);
            tabPage3.Controls.Add(tb_rename_current_name);
            tabPage3.Location = new Point(4, 29);
            tabPage3.Name = "tabPage3";
            tabPage3.Padding = new Padding(3);
            tabPage3.Size = new Size(1319, 730);
            tabPage3.TabIndex = 2;
            tabPage3.Text = "Copy Map with new name";
            tabPage3.UseVisualStyleBackColor = true;
            // 
            // btnZipDeployment2
            // 
            btnZipDeployment2.Location = new Point(192, 252);
            btnZipDeployment2.Name = "btnZipDeployment2";
            btnZipDeployment2.Size = new Size(153, 29);
            btnZipDeployment2.TabIndex = 8;
            btnZipDeployment2.Text = "2. Zip Deployment";
            btnZipDeployment2.UseVisualStyleBackColor = true;
            btnZipDeployment2.Click += btnZipDeployment2_Click;
            // 
            // label6
            // 
            label6.AutoSize = true;
            label6.Location = new Point(9, 175);
            label6.Name = "label6";
            label6.Size = new Size(264, 20);
            label6.TabIndex = 7;
            label6.Text = "Your new name for the title in info.json";
            // 
            // tb_rename_new_name_title
            // 
            tb_rename_new_name_title.Location = new Point(9, 198);
            tb_rename_new_name_title.Name = "tb_rename_new_name_title";
            tb_rename_new_name_title.Size = new Size(346, 27);
            tb_rename_new_name_title.TabIndex = 6;
            // 
            // Btn_RenameLevel
            // 
            Btn_RenameLevel.Location = new Point(9, 252);
            Btn_RenameLevel.Name = "Btn_RenameLevel";
            Btn_RenameLevel.Size = new Size(153, 29);
            Btn_RenameLevel.TabIndex = 5;
            Btn_RenameLevel.Text = "1. Rename Level";
            Btn_RenameLevel.UseVisualStyleBackColor = true;
            Btn_RenameLevel.Click += Btn_RenameLevel_Click;
            // 
            // BtnGetCurrentName
            // 
            BtnGetCurrentName.Location = new Point(374, 47);
            BtnGetCurrentName.Name = "BtnGetCurrentName";
            BtnGetCurrentName.Size = new Size(121, 29);
            BtnGetCurrentName.TabIndex = 4;
            BtnGetCurrentName.Text = "Get Name";
            BtnGetCurrentName.UseVisualStyleBackColor = true;
            BtnGetCurrentName.Click += BtnGetCurrentName_Click;
            // 
            // label5
            // 
            label5.AutoSize = true;
            label5.Location = new Point(8, 104);
            label5.Name = "label5";
            label5.Size = new Size(242, 20);
            label5.TabIndex = 3;
            label5.Text = "Your new name included in filepath";
            // 
            // tb_rename_new_name_path
            // 
            tb_rename_new_name_path.Location = new Point(8, 127);
            tb_rename_new_name_path.Name = "tb_rename_new_name_path";
            tb_rename_new_name_path.Size = new Size(346, 27);
            tb_rename_new_name_path.TabIndex = 2;
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Location = new Point(9, 26);
            label4.Name = "label4";
            label4.Size = new Size(210, 20);
            label4.TabIndex = 1;
            label4.Text = "Current name of selected level";
            // 
            // tb_rename_current_name
            // 
            tb_rename_current_name.Location = new Point(8, 49);
            tb_rename_current_name.Name = "tb_rename_current_name";
            tb_rename_current_name.ReadOnly = true;
            tb_rename_current_name.Size = new Size(346, 27);
            tb_rename_current_name.TabIndex = 0;
            // 
            // tabPage2
            // 
            tabPage2.Controls.Add(richTextBoxErrors);
            tabPage2.Location = new Point(4, 29);
            tabPage2.Name = "tabPage2";
            tabPage2.Padding = new Padding(3);
            tabPage2.Size = new Size(1319, 730);
            tabPage2.TabIndex = 1;
            tabPage2.Text = "Errors";
            tabPage2.UseVisualStyleBackColor = true;
            // 
            // richTextBoxErrors
            // 
            richTextBoxErrors.Dock = DockStyle.Fill;
            richTextBoxErrors.Location = new Point(3, 3);
            richTextBoxErrors.Name = "richTextBoxErrors";
            richTextBoxErrors.Size = new Size(1313, 724);
            richTextBoxErrors.TabIndex = 0;
            richTextBoxErrors.Text = "";
            // 
            // tabPage4
            // 
            tabPage4.Controls.Add(splitContainerCopyAssets);
            tabPage4.Location = new Point(4, 29);
            tabPage4.Name = "tabPage4";
            tabPage4.Padding = new Padding(3);
            tabPage4.Size = new Size(1319, 730);
            tabPage4.TabIndex = 3;
            tabPage4.Text = "Copy Assets";
            tabPage4.UseVisualStyleBackColor = true;
            // 
            // splitContainerCopyAssets
            // 
            splitContainerCopyAssets.Dock = DockStyle.Fill;
            splitContainerCopyAssets.FixedPanel = FixedPanel.Panel1;
            splitContainerCopyAssets.Location = new Point(3, 3);
            splitContainerCopyAssets.Name = "splitContainerCopyAssets";
            splitContainerCopyAssets.Orientation = Orientation.Horizontal;
            // 
            // splitContainerCopyAssets.Panel1
            // 
            splitContainerCopyAssets.Panel1.Controls.Add(labelFileSummaryCopy);
            splitContainerCopyAssets.Panel1.Controls.Add(BtnCopyAssets);
            splitContainerCopyAssets.Panel1.Controls.Add(label8);
            splitContainerCopyAssets.Panel1.Controls.Add(BtnScanAssets);
            splitContainerCopyAssets.Panel1.Controls.Add(BtnCopyFromZipLevel);
            splitContainerCopyAssets.Panel1.Controls.Add(lbLevelNameCopyFrom);
            splitContainerCopyAssets.Panel1.Controls.Add(tbCopyFromLevel);
            splitContainerCopyAssets.Panel1.Controls.Add(label9);
            splitContainerCopyAssets.Panel1MinSize = 110;
            // 
            // splitContainerCopyAssets.Panel2
            // 
            splitContainerCopyAssets.Panel2.Controls.Add(dataGridViewCopyList);
            splitContainerCopyAssets.Panel2.Controls.Add(cbAllNoneCopyList);
            splitContainerCopyAssets.Panel2.Controls.Add(tbFilterGridCopyList);
            splitContainerCopyAssets.Panel2MinSize = 300;
            splitContainerCopyAssets.Size = new Size(1313, 724);
            splitContainerCopyAssets.SplitterDistance = 170;
            splitContainerCopyAssets.TabIndex = 35;
            // 
            // labelFileSummaryCopy
            // 
            labelFileSummaryCopy.AutoSize = true;
            labelFileSummaryCopy.Dock = DockStyle.Right;
            labelFileSummaryCopy.Location = new Point(1255, 0);
            labelFileSummaryCopy.Name = "labelFileSummaryCopy";
            labelFileSummaryCopy.Size = new Size(58, 20);
            labelFileSummaryCopy.TabIndex = 36;
            labelFileSummaryCopy.Text = "label10";
            labelFileSummaryCopy.TextAlign = ContentAlignment.TopRight;
            // 
            // BtnCopyAssets
            // 
            BtnCopyAssets.Location = new Point(183, 118);
            BtnCopyAssets.Name = "BtnCopyAssets";
            BtnCopyAssets.Size = new Size(153, 29);
            BtnCopyAssets.TabIndex = 35;
            BtnCopyAssets.Text = "2. Copy Assets";
            BtnCopyAssets.UseVisualStyleBackColor = true;
            BtnCopyAssets.Click += BtnCopyAssets_Click;
            // 
            // label8
            // 
            label8.AutoSize = true;
            label8.Location = new Point(5, 14);
            label8.Name = "label8";
            label8.Size = new Size(331, 20);
            label8.TabIndex = 29;
            label8.Text = "The zipped map level file you want to copy from";
            // 
            // BtnScanAssets
            // 
            BtnScanAssets.Location = new Point(5, 118);
            BtnScanAssets.Name = "BtnScanAssets";
            BtnScanAssets.Size = new Size(153, 29);
            BtnScanAssets.TabIndex = 34;
            BtnScanAssets.Text = "1. Scan Assets";
            BtnScanAssets.UseVisualStyleBackColor = true;
            BtnScanAssets.Click += BtnScanAssets_Click;
            // 
            // BtnCopyFromZipLevel
            // 
            BtnCopyFromZipLevel.Location = new Point(575, 35);
            BtnCopyFromZipLevel.Name = "BtnCopyFromZipLevel";
            BtnCopyFromZipLevel.Size = new Size(178, 29);
            BtnCopyFromZipLevel.TabIndex = 31;
            BtnCopyFromZipLevel.Text = "Select zip file";
            BtnCopyFromZipLevel.UseVisualStyleBackColor = true;
            BtnCopyFromZipLevel.Click += BtnCopyFromZipLevel_Click;
            // 
            // lbLevelNameCopyFrom
            // 
            lbLevelNameCopyFrom.AutoSize = true;
            lbLevelNameCopyFrom.Location = new Point(96, 82);
            lbLevelNameCopyFrom.Name = "lbLevelNameCopyFrom";
            lbLevelNameCopyFrom.Size = new Size(0, 20);
            lbLevelNameCopyFrom.TabIndex = 33;
            // 
            // tbCopyFromLevel
            // 
            tbCopyFromLevel.Location = new Point(5, 37);
            tbCopyFromLevel.Name = "tbCopyFromLevel";
            tbCopyFromLevel.Size = new Size(564, 27);
            tbCopyFromLevel.TabIndex = 30;
            // 
            // label9
            // 
            label9.AutoSize = true;
            label9.Location = new Point(7, 82);
            label9.Name = "label9";
            label9.Size = new Size(83, 20);
            label9.TabIndex = 32;
            label9.Text = "Levelname:";
            // 
            // dataGridViewCopyList
            // 
            dataGridViewCopyList.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            dataGridViewCopyList.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            dataGridViewCopyList.Dock = DockStyle.Fill;
            dataGridViewCopyList.Location = new Point(0, 51);
            dataGridViewCopyList.Name = "dataGridViewCopyList";
            dataGridViewCopyList.RowHeadersWidth = 51;
            dataGridViewCopyList.RowTemplate.Height = 29;
            dataGridViewCopyList.Size = new Size(1313, 499);
            dataGridViewCopyList.TabIndex = 25;
            dataGridViewCopyList.CellContentClick += dataGridViewCopyList_CellContentClick;
            dataGridViewCopyList.CellValueChanged += dataGridViewCopyList_CellValueChanged;
            // 
            // cbAllNoneCopyList
            // 
            cbAllNoneCopyList.Checked = true;
            cbAllNoneCopyList.CheckState = CheckState.Checked;
            cbAllNoneCopyList.Dock = DockStyle.Top;
            cbAllNoneCopyList.Location = new Point(0, 27);
            cbAllNoneCopyList.Name = "cbAllNoneCopyList";
            cbAllNoneCopyList.Size = new Size(1313, 24);
            cbAllNoneCopyList.TabIndex = 26;
            cbAllNoneCopyList.Text = "Selection All / None";
            cbAllNoneCopyList.UseVisualStyleBackColor = true;
            cbAllNoneCopyList.CheckedChanged += cbAllNoneCopyList_CheckedChanged;
            // 
            // tbFilterGridCopyList
            // 
            tbFilterGridCopyList.Dock = DockStyle.Top;
            tbFilterGridCopyList.Location = new Point(0, 0);
            tbFilterGridCopyList.Name = "tbFilterGridCopyList";
            tbFilterGridCopyList.PlaceholderText = "Search...";
            tbFilterGridCopyList.Size = new Size(1313, 27);
            tbFilterGridCopyList.TabIndex = 27;
            tbFilterGridCopyList.TextChanged += tbFilterGridCopyList_TextChanged;
            // 
            // tabPage5
            // 
            tabPage5.Controls.Add(blazorWebView1);
            tabPage5.Location = new Point(4, 29);
            tabPage5.Name = "tabPage5";
            tabPage5.Size = new Size(1319, 730);
            tabPage5.TabIndex = 4;
            tabPage5.Text = "Blazor";
            tabPage5.UseVisualStyleBackColor = true;
            // 
            // blazorWebView1
            // 
            blazorWebView1.Dock = DockStyle.Fill;
            blazorWebView1.Location = new Point(0, 0);
            blazorWebView1.Name = "blazorWebView1";
            blazorWebView1.Size = new Size(1319, 730);
            blazorWebView1.TabIndex = 0;
            blazorWebView1.Text = "blazorWebView1";
            // 
            // coboCompressionLevel1
            // 
            coboCompressionLevel1.FormattingEnabled = true;
            coboCompressionLevel1.Location = new Point(824, 22);
            coboCompressionLevel1.Name = "coboCompressionLevel1";
            coboCompressionLevel1.Size = new Size(165, 28);
            coboCompressionLevel1.TabIndex = 30;
            // 
            // tbProgress
            // 
            tbProgress.Location = new Point(10, 109);
            tbProgress.Margin = new Padding(2);
            tbProgress.Name = "tbProgress";
            tbProgress.ReadOnly = true;
            tbProgress.Size = new Size(1054, 27);
            tbProgress.TabIndex = 25;
            // 
            // btnLoadLevelZipFile
            // 
            btnLoadLevelZipFile.Location = new Point(582, 22);
            btnLoadLevelZipFile.Name = "btnLoadLevelZipFile";
            btnLoadLevelZipFile.Size = new Size(178, 29);
            btnLoadLevelZipFile.TabIndex = 28;
            btnLoadLevelZipFile.Text = "Select zip file";
            btnLoadLevelZipFile.UseVisualStyleBackColor = true;
            btnLoadLevelZipFile.Click += btnLoadLevelZipFile_Click;
            // 
            // tbLevelZipFile
            // 
            tbLevelZipFile.Location = new Point(12, 24);
            tbLevelZipFile.Name = "tbLevelZipFile";
            tbLevelZipFile.Size = new Size(564, 27);
            tbLevelZipFile.TabIndex = 27;
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Location = new Point(12, 1);
            label3.Name = "label3";
            label3.Size = new Size(182, 20);
            label3.TabIndex = 26;
            label3.Text = "Your zipped map level file";
            // 
            // btn_openLevelFolder
            // 
            btn_openLevelFolder.Location = new Point(582, 75);
            btn_openLevelFolder.Name = "btn_openLevelFolder";
            btn_openLevelFolder.Size = new Size(178, 29);
            btn_openLevelFolder.TabIndex = 15;
            btn_openLevelFolder.Text = "Select Level Folder";
            btn_openLevelFolder.UseVisualStyleBackColor = true;
            btn_openLevelFolder.Click += btn_openLevelFolder_Click;
            // 
            // tbLevelPath
            // 
            tbLevelPath.Location = new Point(12, 77);
            tbLevelPath.Name = "tbLevelPath";
            tbLevelPath.Size = new Size(564, 27);
            tbLevelPath.TabIndex = 14;
            tbLevelPath.TextChanged += textBox1_TextChanged;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(12, 54);
            label1.Name = "label1";
            label1.Size = new Size(255, 20);
            label1.TabIndex = 13;
            label1.Text = "or Level Folder starting with /levels/...";
            // 
            // panel1
            // 
            panel1.Controls.Add(label7);
            panel1.Controls.Add(coboCompressionLevel1);
            panel1.Controls.Add(tbLevelZipFile);
            panel1.Controls.Add(tbProgress);
            panel1.Controls.Add(btnLoadLevelZipFile);
            panel1.Controls.Add(label1);
            panel1.Controls.Add(tbLevelPath);
            panel1.Controls.Add(label3);
            panel1.Controls.Add(btn_openLevelFolder);
            panel1.Dock = DockStyle.Top;
            panel1.Location = new Point(0, 0);
            panel1.Name = "panel1";
            panel1.Size = new Size(1327, 140);
            panel1.TabIndex = 1;
            // 
            // label7
            // 
            label7.AutoSize = true;
            label7.Location = new Point(824, 1);
            label7.Name = "label7";
            label7.Size = new Size(341, 20);
            label7.TabIndex = 31;
            label7.Text = "Compression Level for deployment file generation";
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(8F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1327, 915);
            Controls.Add(tabControl1);
            Controls.Add(panel1);
            Name = "Form1";
            tabControl1.ResumeLayout(false);
            tabPage1.ResumeLayout(false);
            splitContainerShrink.Panel1.ResumeLayout(false);
            splitContainerShrink.Panel1.PerformLayout();
            splitContainerShrink.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)splitContainerShrink).EndInit();
            splitContainerShrink.ResumeLayout(false);
            splitContainer_grid.Panel1.ResumeLayout(false);
            splitContainer_grid.Panel1.PerformLayout();
            splitContainer_grid.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)splitContainer_grid).EndInit();
            splitContainer_grid.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)dataGridViewDeleteList).EndInit();
            tabPage3.ResumeLayout(false);
            tabPage3.PerformLayout();
            tabPage2.ResumeLayout(false);
            tabPage4.ResumeLayout(false);
            splitContainerCopyAssets.Panel1.ResumeLayout(false);
            splitContainerCopyAssets.Panel1.PerformLayout();
            splitContainerCopyAssets.Panel2.ResumeLayout(false);
            splitContainerCopyAssets.Panel2.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)splitContainerCopyAssets).EndInit();
            splitContainerCopyAssets.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)dataGridViewCopyList).EndInit();
            tabPage5.ResumeLayout(false);
            panel1.ResumeLayout(false);
            panel1.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        private TabControl tabControl1;
        private TabPage tabPage1;
        private SplitContainer splitContainerShrink;
        private CheckBox chkDryRun;
        private Button btn_AnalyzeLevel;
        private Button btn_openLevelFolder;
        private TextBox tbLevelPath;
        private Label label1;
        private DataGridView dataGridViewDeleteList;
        private TabPage tabPage2;
        private FolderBrowserDialog folderBrowserDialogLevel;
        private Button btn_deleteFiles;
        private RichTextBox richTextBoxErrors;
        private OpenFileDialog openFileDialogLog;
        private Button btnOpenLog;
        private TextBox tbBeamLogPath;
        private Label label2;
        private Label labelFileSummaryShrink;
        private CheckBox cbAllNoneDeleteList;
        private TextBox tbProgress;
        private Button btnLoadLevelZipFile;
        private TextBox tbLevelZipFile;
        private Label label3;
        private OpenFileDialog openFileDialogZip;
        private Button btnZipDeployment;
        private TabPage tabPage3;
        private Panel panel1;
        private System.ComponentModel.BackgroundWorker backgroundWorker1;
        private Label label5;
        private TextBox tb_rename_new_name_path;
        private Label label4;
        private TextBox tb_rename_current_name;
        private Button BtnGetCurrentName;
        private Button Btn_RenameLevel;
        private Label label6;
        private TextBox tb_rename_new_name_title;
        private Button btnZipDeployment2;
        private TextBox tbFilterGridDeleteList;
        private SplitContainer splitContainer_grid;
        private ComboBox coboCompressionLevel1;
        private Label label7;
        private TabPage tabPage4;
        private SplitContainer splitContainerCopyAssets;
        private Label label8;
        private Button BtnScanAssets;
        private Button BtnCopyFromZipLevel;
        private Label lbLevelNameCopyFrom;
        private TextBox tbCopyFromLevel;
        private Label label9;
        private DataGridView dataGridViewCopyList;
        private CheckBox cbAllNoneCopyList;
        private TextBox tbFilterGridCopyList;
        private Button BtnCopyAssets;
        private Label labelFileSummaryCopy;
        private TabPage tabPage5;
        private Microsoft.AspNetCore.Components.WebView.WindowsForms.BlazorWebView blazorWebView1;
    }
}