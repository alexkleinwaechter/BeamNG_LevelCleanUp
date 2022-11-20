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
            this.tabControl1 = new System.Windows.Forms.TabControl();
            this.tabPage1 = new System.Windows.Forms.TabPage();
            this.splitContainerShrink = new System.Windows.Forms.SplitContainer();
            this.btnZipDeployment = new System.Windows.Forms.Button();
            this.labelFileSummary = new System.Windows.Forms.Label();
            this.btnOpenLog = new System.Windows.Forms.Button();
            this.tbBeamLogPath = new System.Windows.Forms.TextBox();
            this.label2 = new System.Windows.Forms.Label();
            this.btn_deleteFiles = new System.Windows.Forms.Button();
            this.chkDryRun = new System.Windows.Forms.CheckBox();
            this.btn_AnalyzeLevel = new System.Windows.Forms.Button();
            this.splitContainer_grid = new System.Windows.Forms.SplitContainer();
            this.cbAllNone = new System.Windows.Forms.CheckBox();
            this.tbFilterGrid = new System.Windows.Forms.TextBox();
            this.dataGridViewDeleteList = new System.Windows.Forms.DataGridView();
            this.tabPage3 = new System.Windows.Forms.TabPage();
            this.btnZipDeployment2 = new System.Windows.Forms.Button();
            this.label6 = new System.Windows.Forms.Label();
            this.tb_rename_new_name_title = new System.Windows.Forms.TextBox();
            this.Btn_RenameLevel = new System.Windows.Forms.Button();
            this.BtnGetCurrentName = new System.Windows.Forms.Button();
            this.label5 = new System.Windows.Forms.Label();
            this.tb_rename_new_name_path = new System.Windows.Forms.TextBox();
            this.label4 = new System.Windows.Forms.Label();
            this.tb_rename_current_name = new System.Windows.Forms.TextBox();
            this.tabPage2 = new System.Windows.Forms.TabPage();
            this.richTextBoxErrors = new System.Windows.Forms.RichTextBox();
            this.tbProgress = new System.Windows.Forms.TextBox();
            this.btnLoadLevelZipFile = new System.Windows.Forms.Button();
            this.tbLevelZipFile = new System.Windows.Forms.TextBox();
            this.label3 = new System.Windows.Forms.Label();
            this.btn_openLevelFolder = new System.Windows.Forms.Button();
            this.tbLevelPath = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.folderBrowserDialogLevel = new System.Windows.Forms.FolderBrowserDialog();
            this.openFileDialogLog = new System.Windows.Forms.OpenFileDialog();
            this.openFileDialogZip = new System.Windows.Forms.OpenFileDialog();
            this.panel1 = new System.Windows.Forms.Panel();
            this.backgroundWorker1 = new System.ComponentModel.BackgroundWorker();
            this.tabControl1.SuspendLayout();
            this.tabPage1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainerShrink)).BeginInit();
            this.splitContainerShrink.Panel1.SuspendLayout();
            this.splitContainerShrink.Panel2.SuspendLayout();
            this.splitContainerShrink.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_grid)).BeginInit();
            this.splitContainer_grid.Panel1.SuspendLayout();
            this.splitContainer_grid.Panel2.SuspendLayout();
            this.splitContainer_grid.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dataGridViewDeleteList)).BeginInit();
            this.tabPage3.SuspendLayout();
            this.tabPage2.SuspendLayout();
            this.panel1.SuspendLayout();
            this.SuspendLayout();
            // 
            // tabControl1
            // 
            this.tabControl1.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tabControl1.Controls.Add(this.tabPage1);
            this.tabControl1.Controls.Add(this.tabPage3);
            this.tabControl1.Controls.Add(this.tabPage2);
            this.tabControl1.Location = new System.Drawing.Point(0, 140);
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(1327, 763);
            this.tabControl1.TabIndex = 0;
            // 
            // tabPage1
            // 
            this.tabPage1.Controls.Add(this.splitContainerShrink);
            this.tabPage1.Location = new System.Drawing.Point(4, 29);
            this.tabPage1.Name = "tabPage1";
            this.tabPage1.Padding = new System.Windows.Forms.Padding(3);
            this.tabPage1.Size = new System.Drawing.Size(1319, 730);
            this.tabPage1.TabIndex = 0;
            this.tabPage1.Text = "Shrink Deploymentfile";
            this.tabPage1.UseVisualStyleBackColor = true;
            // 
            // splitContainerShrink
            // 
            this.splitContainerShrink.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainerShrink.FixedPanel = System.Windows.Forms.FixedPanel.Panel1;
            this.splitContainerShrink.Location = new System.Drawing.Point(3, 3);
            this.splitContainerShrink.Name = "splitContainerShrink";
            this.splitContainerShrink.Orientation = System.Windows.Forms.Orientation.Horizontal;
            // 
            // splitContainerShrink.Panel1
            // 
            this.splitContainerShrink.Panel1.Controls.Add(this.btnZipDeployment);
            this.splitContainerShrink.Panel1.Controls.Add(this.labelFileSummary);
            this.splitContainerShrink.Panel1.Controls.Add(this.btnOpenLog);
            this.splitContainerShrink.Panel1.Controls.Add(this.tbBeamLogPath);
            this.splitContainerShrink.Panel1.Controls.Add(this.label2);
            this.splitContainerShrink.Panel1.Controls.Add(this.btn_deleteFiles);
            this.splitContainerShrink.Panel1.Controls.Add(this.chkDryRun);
            this.splitContainerShrink.Panel1.Controls.Add(this.btn_AnalyzeLevel);
            this.splitContainerShrink.Panel1.Padding = new System.Windows.Forms.Padding(10);
            this.splitContainerShrink.Panel1MinSize = 100;
            // 
            // splitContainerShrink.Panel2
            // 
            this.splitContainerShrink.Panel2.AutoScroll = true;
            this.splitContainerShrink.Panel2.Controls.Add(this.splitContainer_grid);
            this.splitContainerShrink.Panel2.Padding = new System.Windows.Forms.Padding(10);
            this.splitContainerShrink.Panel2MinSize = 300;
            this.splitContainerShrink.Size = new System.Drawing.Size(1313, 724);
            this.splitContainerShrink.SplitterDistance = 170;
            this.splitContainerShrink.TabIndex = 14;
            // 
            // btnZipDeployment
            // 
            this.btnZipDeployment.Location = new System.Drawing.Point(337, 125);
            this.btnZipDeployment.Name = "btnZipDeployment";
            this.btnZipDeployment.Size = new System.Drawing.Size(146, 29);
            this.btnZipDeployment.TabIndex = 29;
            this.btnZipDeployment.Text = "3. Zip Deployment";
            this.btnZipDeployment.TextImageRelation = System.Windows.Forms.TextImageRelation.TextAboveImage;
            this.btnZipDeployment.UseVisualStyleBackColor = true;
            this.btnZipDeployment.Click += new System.EventHandler(this.btnZipDeployment_Click);
            // 
            // labelFileSummary
            // 
            this.labelFileSummary.AutoSize = true;
            this.labelFileSummary.Dock = System.Windows.Forms.DockStyle.Right;
            this.labelFileSummary.Location = new System.Drawing.Point(1253, 10);
            this.labelFileSummary.Name = "labelFileSummary";
            this.labelFileSummary.Size = new System.Drawing.Size(50, 20);
            this.labelFileSummary.TabIndex = 24;
            this.labelFileSummary.Text = "label2";
            // 
            // btnOpenLog
            // 
            this.btnOpenLog.Location = new System.Drawing.Point(578, 48);
            this.btnOpenLog.Name = "btnOpenLog";
            this.btnOpenLog.Size = new System.Drawing.Size(178, 29);
            this.btnOpenLog.TabIndex = 22;
            this.btnOpenLog.Text = "Select BeamNg.log";
            this.btnOpenLog.UseVisualStyleBackColor = true;
            this.btnOpenLog.Click += new System.EventHandler(this.btnOpenLog_Click);
            // 
            // tbBeamLogPath
            // 
            this.tbBeamLogPath.Location = new System.Drawing.Point(8, 50);
            this.tbBeamLogPath.Name = "tbBeamLogPath";
            this.tbBeamLogPath.Size = new System.Drawing.Size(564, 27);
            this.tbBeamLogPath.TabIndex = 21;
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(18, 27);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(295, 20);
            this.label2.TabIndex = 20;
            this.label2.Text = "BeamNg.log for excluding Errors in 2nd run";
            // 
            // btn_deleteFiles
            // 
            this.btn_deleteFiles.Location = new System.Drawing.Point(185, 125);
            this.btn_deleteFiles.Name = "btn_deleteFiles";
            this.btn_deleteFiles.Size = new System.Drawing.Size(117, 29);
            this.btn_deleteFiles.TabIndex = 18;
            this.btn_deleteFiles.Text = "2. Delete Files";
            this.btn_deleteFiles.TextImageRelation = System.Windows.Forms.TextImageRelation.TextAboveImage;
            this.btn_deleteFiles.UseVisualStyleBackColor = true;
            this.btn_deleteFiles.Click += new System.EventHandler(this.btn_deleteFiles_Click);
            // 
            // chkDryRun
            // 
            this.chkDryRun.AutoSize = true;
            this.chkDryRun.Checked = true;
            this.chkDryRun.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkDryRun.Location = new System.Drawing.Point(10, 83);
            this.chkDryRun.Name = "chkDryRun";
            this.chkDryRun.Size = new System.Drawing.Size(198, 24);
            this.chkDryRun.TabIndex = 17;
            this.chkDryRun.Text = "Dry Run without Deletion";
            this.chkDryRun.UseVisualStyleBackColor = true;
            // 
            // btn_AnalyzeLevel
            // 
            this.btn_AnalyzeLevel.Location = new System.Drawing.Point(7, 125);
            this.btn_AnalyzeLevel.Name = "btn_AnalyzeLevel";
            this.btn_AnalyzeLevel.Size = new System.Drawing.Size(142, 29);
            this.btn_AnalyzeLevel.TabIndex = 16;
            this.btn_AnalyzeLevel.Text = "1. Analyze Level";
            this.btn_AnalyzeLevel.UseVisualStyleBackColor = true;
            this.btn_AnalyzeLevel.Click += new System.EventHandler(this.btn_AnalyzeLevel_Click);
            // 
            // splitContainer_grid
            // 
            this.splitContainer_grid.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer_grid.FixedPanel = System.Windows.Forms.FixedPanel.Panel1;
            this.splitContainer_grid.Location = new System.Drawing.Point(10, 10);
            this.splitContainer_grid.Name = "splitContainer_grid";
            this.splitContainer_grid.Orientation = System.Windows.Forms.Orientation.Horizontal;
            // 
            // splitContainer_grid.Panel1
            // 
            this.splitContainer_grid.Panel1.Controls.Add(this.cbAllNone);
            this.splitContainer_grid.Panel1.Controls.Add(this.tbFilterGrid);
            this.splitContainer_grid.Panel1MinSize = 50;
            // 
            // splitContainer_grid.Panel2
            // 
            this.splitContainer_grid.Panel2.Controls.Add(this.dataGridViewDeleteList);
            this.splitContainer_grid.Panel2MinSize = 300;
            this.splitContainer_grid.Size = new System.Drawing.Size(1293, 530);
            this.splitContainer_grid.SplitterDistance = 51;
            this.splitContainer_grid.TabIndex = 25;
            // 
            // cbAllNone
            // 
            this.cbAllNone.Checked = true;
            this.cbAllNone.CheckState = System.Windows.Forms.CheckState.Checked;
            this.cbAllNone.Dock = System.Windows.Forms.DockStyle.Top;
            this.cbAllNone.Location = new System.Drawing.Point(0, 27);
            this.cbAllNone.Name = "cbAllNone";
            this.cbAllNone.Size = new System.Drawing.Size(1293, 24);
            this.cbAllNone.TabIndex = 23;
            this.cbAllNone.Text = "Selection All / None";
            this.cbAllNone.UseVisualStyleBackColor = true;
            this.cbAllNone.CheckedChanged += new System.EventHandler(this.cbAllNone_CheckedChanged);
            // 
            // tbFilterGrid
            // 
            this.tbFilterGrid.Dock = System.Windows.Forms.DockStyle.Top;
            this.tbFilterGrid.Location = new System.Drawing.Point(0, 0);
            this.tbFilterGrid.Name = "tbFilterGrid";
            this.tbFilterGrid.PlaceholderText = "Search...";
            this.tbFilterGrid.Size = new System.Drawing.Size(1293, 27);
            this.tbFilterGrid.TabIndex = 24;
            this.tbFilterGrid.TextChanged += new System.EventHandler(this.tbFilterGrid_TextChanged);
            // 
            // dataGridViewDeleteList
            // 
            this.dataGridViewDeleteList.AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.DisplayedCells;
            this.dataGridViewDeleteList.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dataGridViewDeleteList.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dataGridViewDeleteList.Location = new System.Drawing.Point(0, 0);
            this.dataGridViewDeleteList.Name = "dataGridViewDeleteList";
            this.dataGridViewDeleteList.RowHeadersWidth = 51;
            this.dataGridViewDeleteList.RowTemplate.Height = 29;
            this.dataGridViewDeleteList.Size = new System.Drawing.Size(1293, 475);
            this.dataGridViewDeleteList.TabIndex = 0;
            this.dataGridViewDeleteList.CellContentClick += new System.Windows.Forms.DataGridViewCellEventHandler(this.dataGridViewDeleteList_CellContentClick);
            this.dataGridViewDeleteList.CellValueChanged += new System.Windows.Forms.DataGridViewCellEventHandler(this.dataGridViewDeleteList_CellValueChanged);
            this.dataGridViewDeleteList.CurrentCellDirtyStateChanged += new System.EventHandler(this.dataGridViewDeleteList_CurrentCellDirtyStateChanged);
            // 
            // tabPage3
            // 
            this.tabPage3.Controls.Add(this.btnZipDeployment2);
            this.tabPage3.Controls.Add(this.label6);
            this.tabPage3.Controls.Add(this.tb_rename_new_name_title);
            this.tabPage3.Controls.Add(this.Btn_RenameLevel);
            this.tabPage3.Controls.Add(this.BtnGetCurrentName);
            this.tabPage3.Controls.Add(this.label5);
            this.tabPage3.Controls.Add(this.tb_rename_new_name_path);
            this.tabPage3.Controls.Add(this.label4);
            this.tabPage3.Controls.Add(this.tb_rename_current_name);
            this.tabPage3.Location = new System.Drawing.Point(4, 29);
            this.tabPage3.Name = "tabPage3";
            this.tabPage3.Padding = new System.Windows.Forms.Padding(3);
            this.tabPage3.Size = new System.Drawing.Size(1319, 730);
            this.tabPage3.TabIndex = 2;
            this.tabPage3.Text = "Copy Map with new name";
            this.tabPage3.UseVisualStyleBackColor = true;
            // 
            // btnZipDeployment2
            // 
            this.btnZipDeployment2.Location = new System.Drawing.Point(192, 252);
            this.btnZipDeployment2.Name = "btnZipDeployment2";
            this.btnZipDeployment2.Size = new System.Drawing.Size(153, 29);
            this.btnZipDeployment2.TabIndex = 8;
            this.btnZipDeployment2.Text = "2. Zip Deployment";
            this.btnZipDeployment2.UseVisualStyleBackColor = true;
            this.btnZipDeployment2.Click += new System.EventHandler(this.btnZipDeployment2_Click);
            // 
            // label6
            // 
            this.label6.AutoSize = true;
            this.label6.Location = new System.Drawing.Point(9, 175);
            this.label6.Name = "label6";
            this.label6.Size = new System.Drawing.Size(264, 20);
            this.label6.TabIndex = 7;
            this.label6.Text = "Your new name for the title in info.json";
            // 
            // tb_rename_new_name_title
            // 
            this.tb_rename_new_name_title.Location = new System.Drawing.Point(9, 198);
            this.tb_rename_new_name_title.Name = "tb_rename_new_name_title";
            this.tb_rename_new_name_title.Size = new System.Drawing.Size(346, 27);
            this.tb_rename_new_name_title.TabIndex = 6;
            // 
            // Btn_RenameLevel
            // 
            this.Btn_RenameLevel.Location = new System.Drawing.Point(9, 252);
            this.Btn_RenameLevel.Name = "Btn_RenameLevel";
            this.Btn_RenameLevel.Size = new System.Drawing.Size(153, 29);
            this.Btn_RenameLevel.TabIndex = 5;
            this.Btn_RenameLevel.Text = "1. Rename Level";
            this.Btn_RenameLevel.UseVisualStyleBackColor = true;
            this.Btn_RenameLevel.Click += new System.EventHandler(this.Btn_RenameLevel_Click);
            // 
            // BtnGetCurrentName
            // 
            this.BtnGetCurrentName.Location = new System.Drawing.Point(374, 47);
            this.BtnGetCurrentName.Name = "BtnGetCurrentName";
            this.BtnGetCurrentName.Size = new System.Drawing.Size(121, 29);
            this.BtnGetCurrentName.TabIndex = 4;
            this.BtnGetCurrentName.Text = "Get Name";
            this.BtnGetCurrentName.UseVisualStyleBackColor = true;
            this.BtnGetCurrentName.Click += new System.EventHandler(this.BtnGetCurrentName_Click);
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Location = new System.Drawing.Point(8, 104);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(242, 20);
            this.label5.TabIndex = 3;
            this.label5.Text = "Your new name included in filepath";
            // 
            // tb_rename_new_name_path
            // 
            this.tb_rename_new_name_path.Location = new System.Drawing.Point(8, 127);
            this.tb_rename_new_name_path.Name = "tb_rename_new_name_path";
            this.tb_rename_new_name_path.Size = new System.Drawing.Size(346, 27);
            this.tb_rename_new_name_path.TabIndex = 2;
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(9, 26);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(210, 20);
            this.label4.TabIndex = 1;
            this.label4.Text = "Current name of selected level";
            // 
            // tb_rename_current_name
            // 
            this.tb_rename_current_name.Location = new System.Drawing.Point(8, 49);
            this.tb_rename_current_name.Name = "tb_rename_current_name";
            this.tb_rename_current_name.ReadOnly = true;
            this.tb_rename_current_name.Size = new System.Drawing.Size(346, 27);
            this.tb_rename_current_name.TabIndex = 0;
            // 
            // tabPage2
            // 
            this.tabPage2.Controls.Add(this.richTextBoxErrors);
            this.tabPage2.Location = new System.Drawing.Point(4, 29);
            this.tabPage2.Name = "tabPage2";
            this.tabPage2.Padding = new System.Windows.Forms.Padding(3);
            this.tabPage2.Size = new System.Drawing.Size(1319, 730);
            this.tabPage2.TabIndex = 1;
            this.tabPage2.Text = "Errors";
            this.tabPage2.UseVisualStyleBackColor = true;
            // 
            // richTextBoxErrors
            // 
            this.richTextBoxErrors.Dock = System.Windows.Forms.DockStyle.Fill;
            this.richTextBoxErrors.Location = new System.Drawing.Point(3, 3);
            this.richTextBoxErrors.Name = "richTextBoxErrors";
            this.richTextBoxErrors.Size = new System.Drawing.Size(1313, 724);
            this.richTextBoxErrors.TabIndex = 0;
            this.richTextBoxErrors.Text = "";
            // 
            // tbProgress
            // 
            this.tbProgress.Location = new System.Drawing.Point(10, 109);
            this.tbProgress.Margin = new System.Windows.Forms.Padding(2);
            this.tbProgress.Name = "tbProgress";
            this.tbProgress.ReadOnly = true;
            this.tbProgress.Size = new System.Drawing.Size(1054, 27);
            this.tbProgress.TabIndex = 25;
            // 
            // btnLoadLevelZipFile
            // 
            this.btnLoadLevelZipFile.Location = new System.Drawing.Point(582, 22);
            this.btnLoadLevelZipFile.Name = "btnLoadLevelZipFile";
            this.btnLoadLevelZipFile.Size = new System.Drawing.Size(178, 29);
            this.btnLoadLevelZipFile.TabIndex = 28;
            this.btnLoadLevelZipFile.Text = "Select zip file";
            this.btnLoadLevelZipFile.UseVisualStyleBackColor = true;
            this.btnLoadLevelZipFile.Click += new System.EventHandler(this.btnLoadLevelZipFile_Click);
            // 
            // tbLevelZipFile
            // 
            this.tbLevelZipFile.Location = new System.Drawing.Point(12, 24);
            this.tbLevelZipFile.Name = "tbLevelZipFile";
            this.tbLevelZipFile.Size = new System.Drawing.Size(564, 27);
            this.tbLevelZipFile.TabIndex = 27;
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(12, 1);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(151, 20);
            this.label3.TabIndex = 26;
            this.label3.Text = "Zipped map level file";
            // 
            // btn_openLevelFolder
            // 
            this.btn_openLevelFolder.Location = new System.Drawing.Point(582, 75);
            this.btn_openLevelFolder.Name = "btn_openLevelFolder";
            this.btn_openLevelFolder.Size = new System.Drawing.Size(178, 29);
            this.btn_openLevelFolder.TabIndex = 15;
            this.btn_openLevelFolder.Text = "Select Level Folder";
            this.btn_openLevelFolder.UseVisualStyleBackColor = true;
            this.btn_openLevelFolder.Click += new System.EventHandler(this.btn_openLevelFolder_Click);
            // 
            // tbLevelPath
            // 
            this.tbLevelPath.Location = new System.Drawing.Point(12, 77);
            this.tbLevelPath.Name = "tbLevelPath";
            this.tbLevelPath.Size = new System.Drawing.Size(564, 27);
            this.tbLevelPath.TabIndex = 14;
            this.tbLevelPath.TextChanged += new System.EventHandler(this.textBox1_TextChanged);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(12, 54);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(255, 20);
            this.label1.TabIndex = 13;
            this.label1.Text = "or Level Folder starting with /levels/...";
            // 
            // panel1
            // 
            this.panel1.Controls.Add(this.tbLevelZipFile);
            this.panel1.Controls.Add(this.tbProgress);
            this.panel1.Controls.Add(this.btnLoadLevelZipFile);
            this.panel1.Controls.Add(this.label1);
            this.panel1.Controls.Add(this.tbLevelPath);
            this.panel1.Controls.Add(this.label3);
            this.panel1.Controls.Add(this.btn_openLevelFolder);
            this.panel1.Dock = System.Windows.Forms.DockStyle.Top;
            this.panel1.Location = new System.Drawing.Point(0, 0);
            this.panel1.Name = "panel1";
            this.panel1.Size = new System.Drawing.Size(1327, 140);
            this.panel1.TabIndex = 1;
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 20F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1327, 915);
            this.Controls.Add(this.tabControl1);
            this.Controls.Add(this.panel1);
            this.Name = "Form1";
            this.Text = "BeamNG Tools for Mapbuilders - version 0.83";
            this.tabControl1.ResumeLayout(false);
            this.tabPage1.ResumeLayout(false);
            this.splitContainerShrink.Panel1.ResumeLayout(false);
            this.splitContainerShrink.Panel1.PerformLayout();
            this.splitContainerShrink.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainerShrink)).EndInit();
            this.splitContainerShrink.ResumeLayout(false);
            this.splitContainer_grid.Panel1.ResumeLayout(false);
            this.splitContainer_grid.Panel1.PerformLayout();
            this.splitContainer_grid.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_grid)).EndInit();
            this.splitContainer_grid.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dataGridViewDeleteList)).EndInit();
            this.tabPage3.ResumeLayout(false);
            this.tabPage3.PerformLayout();
            this.tabPage2.ResumeLayout(false);
            this.panel1.ResumeLayout(false);
            this.panel1.PerformLayout();
            this.ResumeLayout(false);

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
        private Label labelFileSummary;
        private CheckBox cbAllNone;
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
        private TextBox tbFilterGrid;
        private SplitContainer splitContainer_grid;
    }
}