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
            this.splitContainer1 = new System.Windows.Forms.SplitContainer();
            this.tbProgress = new System.Windows.Forms.TextBox();
            this.labelFileSummary = new System.Windows.Forms.Label();
            this.cbAllNone = new System.Windows.Forms.CheckBox();
            this.btnOpenLog = new System.Windows.Forms.Button();
            this.textBox2 = new System.Windows.Forms.TextBox();
            this.label2 = new System.Windows.Forms.Label();
            this.btn_deleteFiles = new System.Windows.Forms.Button();
            this.chkDryRun = new System.Windows.Forms.CheckBox();
            this.btn_AnalyzeLevel = new System.Windows.Forms.Button();
            this.btn_openLevelFolder = new System.Windows.Forms.Button();
            this.textBox1 = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.dataGridViewDeleteList = new System.Windows.Forms.DataGridView();
            this.tabPage2 = new System.Windows.Forms.TabPage();
            this.richTextBoxErrors = new System.Windows.Forms.RichTextBox();
            this.folderBrowserDialogLevel = new System.Windows.Forms.FolderBrowserDialog();
            this.openFileDialogLog = new System.Windows.Forms.OpenFileDialog();
            this.tabControl1.SuspendLayout();
            this.tabPage1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer1)).BeginInit();
            this.splitContainer1.Panel1.SuspendLayout();
            this.splitContainer1.Panel2.SuspendLayout();
            this.splitContainer1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dataGridViewDeleteList)).BeginInit();
            this.tabPage2.SuspendLayout();
            this.SuspendLayout();
            // 
            // tabControl1
            // 
            this.tabControl1.Controls.Add(this.tabPage1);
            this.tabControl1.Controls.Add(this.tabPage2);
            this.tabControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tabControl1.Location = new System.Drawing.Point(0, 0);
            this.tabControl1.Margin = new System.Windows.Forms.Padding(4);
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(1606, 1070);
            this.tabControl1.TabIndex = 0;
            // 
            // tabPage1
            // 
            this.tabPage1.Controls.Add(this.splitContainer1);
            this.tabPage1.Location = new System.Drawing.Point(4, 39);
            this.tabPage1.Margin = new System.Windows.Forms.Padding(4);
            this.tabPage1.Name = "tabPage1";
            this.tabPage1.Padding = new System.Windows.Forms.Padding(4);
            this.tabPage1.Size = new System.Drawing.Size(1598, 1027);
            this.tabPage1.TabIndex = 0;
            this.tabPage1.Text = "Shrink Deployment";
            this.tabPage1.UseVisualStyleBackColor = true;
            // 
            // splitContainer1
            // 
            this.splitContainer1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer1.Location = new System.Drawing.Point(4, 4);
            this.splitContainer1.Margin = new System.Windows.Forms.Padding(4);
            this.splitContainer1.Name = "splitContainer1";
            this.splitContainer1.Orientation = System.Windows.Forms.Orientation.Horizontal;
            // 
            // splitContainer1.Panel1
            // 
            this.splitContainer1.Panel1.Controls.Add(this.tbProgress);
            this.splitContainer1.Panel1.Controls.Add(this.labelFileSummary);
            this.splitContainer1.Panel1.Controls.Add(this.cbAllNone);
            this.splitContainer1.Panel1.Controls.Add(this.btnOpenLog);
            this.splitContainer1.Panel1.Controls.Add(this.textBox2);
            this.splitContainer1.Panel1.Controls.Add(this.label2);
            this.splitContainer1.Panel1.Controls.Add(this.btn_deleteFiles);
            this.splitContainer1.Panel1.Controls.Add(this.chkDryRun);
            this.splitContainer1.Panel1.Controls.Add(this.btn_AnalyzeLevel);
            this.splitContainer1.Panel1.Controls.Add(this.btn_openLevelFolder);
            this.splitContainer1.Panel1.Controls.Add(this.textBox1);
            this.splitContainer1.Panel1.Controls.Add(this.label1);
            // 
            // splitContainer1.Panel2
            // 
            this.splitContainer1.Panel2.Controls.Add(this.dataGridViewDeleteList);
            this.splitContainer1.Size = new System.Drawing.Size(1590, 1019);
            this.splitContainer1.SplitterDistance = 391;
            this.splitContainer1.SplitterWidth = 6;
            this.splitContainer1.TabIndex = 14;
            // 
            // tbProgress
            // 
            this.tbProgress.Location = new System.Drawing.Point(28, 315);
            this.tbProgress.Name = "tbProgress";
            this.tbProgress.ReadOnly = true;
            this.tbProgress.Size = new System.Drawing.Size(1123, 35);
            this.tbProgress.TabIndex = 25;
            // 
            // labelFileSummary
            // 
            this.labelFileSummary.AutoSize = true;
            this.labelFileSummary.Dock = System.Windows.Forms.DockStyle.Right;
            this.labelFileSummary.Location = new System.Drawing.Point(1522, 0);
            this.labelFileSummary.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelFileSummary.Name = "labelFileSummary";
            this.labelFileSummary.Size = new System.Drawing.Size(68, 30);
            this.labelFileSummary.TabIndex = 24;
            this.labelFileSummary.Text = "label2";
            // 
            // cbAllNone
            // 
            this.cbAllNone.AutoSize = true;
            this.cbAllNone.Checked = true;
            this.cbAllNone.CheckState = System.Windows.Forms.CheckState.Checked;
            this.cbAllNone.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.cbAllNone.Location = new System.Drawing.Point(0, 357);
            this.cbAllNone.Margin = new System.Windows.Forms.Padding(4);
            this.cbAllNone.Name = "cbAllNone";
            this.cbAllNone.Size = new System.Drawing.Size(1590, 34);
            this.cbAllNone.TabIndex = 23;
            this.cbAllNone.Text = "Selection All / None";
            this.cbAllNone.UseVisualStyleBackColor = true;
            // 
            // btnOpenLog
            // 
            this.btnOpenLog.Location = new System.Drawing.Point(884, 144);
            this.btnOpenLog.Margin = new System.Windows.Forms.Padding(4);
            this.btnOpenLog.Name = "btnOpenLog";
            this.btnOpenLog.Size = new System.Drawing.Size(267, 44);
            this.btnOpenLog.TabIndex = 22;
            this.btnOpenLog.Text = "Select BeamNg.log";
            this.btnOpenLog.UseVisualStyleBackColor = true;
            this.btnOpenLog.Click += new System.EventHandler(this.btnOpenLog_Click);
            // 
            // textBox2
            // 
            this.textBox2.Location = new System.Drawing.Point(28, 147);
            this.textBox2.Margin = new System.Windows.Forms.Padding(4);
            this.textBox2.Name = "textBox2";
            this.textBox2.Size = new System.Drawing.Size(844, 35);
            this.textBox2.TabIndex = 21;
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(28, 112);
            this.label2.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(414, 30);
            this.label2.TabIndex = 20;
            this.label2.Text = "BeamNg.log for excluding Errors in 2nd run";
            // 
            // btn_deleteFiles
            // 
            this.btn_deleteFiles.Location = new System.Drawing.Point(296, 242);
            this.btn_deleteFiles.Margin = new System.Windows.Forms.Padding(4);
            this.btn_deleteFiles.Name = "btn_deleteFiles";
            this.btn_deleteFiles.Size = new System.Drawing.Size(141, 44);
            this.btn_deleteFiles.TabIndex = 18;
            this.btn_deleteFiles.Text = "Delete Files";
            this.btn_deleteFiles.TextImageRelation = System.Windows.Forms.TextImageRelation.TextAboveImage;
            this.btn_deleteFiles.UseVisualStyleBackColor = true;
            this.btn_deleteFiles.Click += new System.EventHandler(this.btn_deleteFiles_Click);
            // 
            // chkDryRun
            // 
            this.chkDryRun.AutoSize = true;
            this.chkDryRun.Checked = true;
            this.chkDryRun.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkDryRun.Location = new System.Drawing.Point(33, 196);
            this.chkDryRun.Margin = new System.Windows.Forms.Padding(4);
            this.chkDryRun.Name = "chkDryRun";
            this.chkDryRun.Size = new System.Drawing.Size(274, 34);
            this.chkDryRun.TabIndex = 17;
            this.chkDryRun.Text = "Dry Run without Deletion";
            this.chkDryRun.UseVisualStyleBackColor = true;
            // 
            // btn_AnalyzeLevel
            // 
            this.btn_AnalyzeLevel.Location = new System.Drawing.Point(28, 242);
            this.btn_AnalyzeLevel.Margin = new System.Windows.Forms.Padding(4);
            this.btn_AnalyzeLevel.Name = "btn_AnalyzeLevel";
            this.btn_AnalyzeLevel.Size = new System.Drawing.Size(213, 44);
            this.btn_AnalyzeLevel.TabIndex = 16;
            this.btn_AnalyzeLevel.Text = "Analyze Level";
            this.btn_AnalyzeLevel.UseVisualStyleBackColor = true;
            this.btn_AnalyzeLevel.Click += new System.EventHandler(this.btn_AnalyzeLevel_Click);
            // 
            // btn_openLevelFolder
            // 
            this.btn_openLevelFolder.Location = new System.Drawing.Point(884, 62);
            this.btn_openLevelFolder.Margin = new System.Windows.Forms.Padding(4);
            this.btn_openLevelFolder.Name = "btn_openLevelFolder";
            this.btn_openLevelFolder.Size = new System.Drawing.Size(267, 44);
            this.btn_openLevelFolder.TabIndex = 15;
            this.btn_openLevelFolder.Text = "Select Level Folder";
            this.btn_openLevelFolder.UseVisualStyleBackColor = true;
            this.btn_openLevelFolder.Click += new System.EventHandler(this.btn_openLevelFolder_Click);
            // 
            // textBox1
            // 
            this.textBox1.Location = new System.Drawing.Point(28, 64);
            this.textBox1.Margin = new System.Windows.Forms.Padding(4);
            this.textBox1.Name = "textBox1";
            this.textBox1.Size = new System.Drawing.Size(844, 35);
            this.textBox1.TabIndex = 14;
            this.textBox1.TextChanged += new System.EventHandler(this.textBox1_TextChanged);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(28, 30);
            this.label1.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(123, 30);
            this.label1.TabIndex = 13;
            this.label1.Text = "Level Folder";
            // 
            // dataGridViewDeleteList
            // 
            this.dataGridViewDeleteList.AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill;
            this.dataGridViewDeleteList.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dataGridViewDeleteList.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dataGridViewDeleteList.Location = new System.Drawing.Point(0, 0);
            this.dataGridViewDeleteList.Margin = new System.Windows.Forms.Padding(4);
            this.dataGridViewDeleteList.Name = "dataGridViewDeleteList";
            this.dataGridViewDeleteList.RowHeadersWidth = 51;
            this.dataGridViewDeleteList.RowTemplate.Height = 29;
            this.dataGridViewDeleteList.Size = new System.Drawing.Size(1590, 622);
            this.dataGridViewDeleteList.TabIndex = 0;
            this.dataGridViewDeleteList.CellContentClick += new System.Windows.Forms.DataGridViewCellEventHandler(this.dataGridViewDeleteList_CellContentClick);
            this.dataGridViewDeleteList.CellValueChanged += new System.Windows.Forms.DataGridViewCellEventHandler(this.dataGridViewDeleteList_CellValueChanged);
            this.dataGridViewDeleteList.CurrentCellDirtyStateChanged += new System.EventHandler(this.dataGridViewDeleteList_CurrentCellDirtyStateChanged);
            // 
            // tabPage2
            // 
            this.tabPage2.Controls.Add(this.richTextBoxErrors);
            this.tabPage2.Location = new System.Drawing.Point(4, 39);
            this.tabPage2.Margin = new System.Windows.Forms.Padding(4);
            this.tabPage2.Name = "tabPage2";
            this.tabPage2.Padding = new System.Windows.Forms.Padding(4);
            this.tabPage2.Size = new System.Drawing.Size(1598, 1027);
            this.tabPage2.TabIndex = 1;
            this.tabPage2.Text = "Errors";
            this.tabPage2.UseVisualStyleBackColor = true;
            // 
            // richTextBoxErrors
            // 
            this.richTextBoxErrors.Dock = System.Windows.Forms.DockStyle.Fill;
            this.richTextBoxErrors.Location = new System.Drawing.Point(4, 4);
            this.richTextBoxErrors.Margin = new System.Windows.Forms.Padding(4);
            this.richTextBoxErrors.Name = "richTextBoxErrors";
            this.richTextBoxErrors.Size = new System.Drawing.Size(1590, 1019);
            this.richTextBoxErrors.TabIndex = 0;
            this.richTextBoxErrors.Text = "";
            // 
            // openFileDialogLog
            // 
            this.openFileDialogLog.FileName = "openFileDialogLog";
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(12F, 30F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1606, 1070);
            this.Controls.Add(this.tabControl1);
            this.Margin = new System.Windows.Forms.Padding(4);
            this.Name = "Form1";
            this.Text = "BeamNG Level Cleanup";
            this.tabControl1.ResumeLayout(false);
            this.tabPage1.ResumeLayout(false);
            this.splitContainer1.Panel1.ResumeLayout(false);
            this.splitContainer1.Panel1.PerformLayout();
            this.splitContainer1.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer1)).EndInit();
            this.splitContainer1.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dataGridViewDeleteList)).EndInit();
            this.tabPage2.ResumeLayout(false);
            this.ResumeLayout(false);

        }

        #endregion

        private TabControl tabControl1;
        private TabPage tabPage1;
        private SplitContainer splitContainer1;
        private CheckBox chkDryRun;
        private Button btn_AnalyzeLevel;
        private Button btn_openLevelFolder;
        private TextBox textBox1;
        private Label label1;
        private DataGridView dataGridViewDeleteList;
        private TabPage tabPage2;
        private FolderBrowserDialog folderBrowserDialogLevel;
        private Button btn_deleteFiles;
        private RichTextBox richTextBoxErrors;
        private OpenFileDialog openFileDialogLog;
        private Button btnOpenLog;
        private TextBox textBox2;
        private Label label2;
        private Label labelFileSummary;
        private CheckBox cbAllNone;
        private TextBox tbProgress;
    }
}