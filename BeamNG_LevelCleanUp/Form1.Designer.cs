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
            this.labelProgress = new System.Windows.Forms.Label();
            this.btn_deleteFiles = new System.Windows.Forms.Button();
            this.chkDryRun = new System.Windows.Forms.CheckBox();
            this.btn_AnalyzeLevel = new System.Windows.Forms.Button();
            this.btn_openLevelFolder = new System.Windows.Forms.Button();
            this.textBox1 = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.labelFileSummary = new System.Windows.Forms.Label();
            this.cbAllNone = new System.Windows.Forms.CheckBox();
            this.dataGridViewDeleteList = new System.Windows.Forms.DataGridView();
            this.tabPage2 = new System.Windows.Forms.TabPage();
            this.richTextBoxErrors = new System.Windows.Forms.RichTextBox();
            this.folderBrowserDialog1 = new System.Windows.Forms.FolderBrowserDialog();
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
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(1071, 713);
            this.tabControl1.TabIndex = 0;
            // 
            // tabPage1
            // 
            this.tabPage1.Controls.Add(this.splitContainer1);
            this.tabPage1.Location = new System.Drawing.Point(4, 29);
            this.tabPage1.Name = "tabPage1";
            this.tabPage1.Padding = new System.Windows.Forms.Padding(3);
            this.tabPage1.Size = new System.Drawing.Size(1063, 680);
            this.tabPage1.TabIndex = 0;
            this.tabPage1.Text = "Shrink Deployment";
            this.tabPage1.UseVisualStyleBackColor = true;
            // 
            // splitContainer1
            // 
            this.splitContainer1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer1.Location = new System.Drawing.Point(3, 3);
            this.splitContainer1.Name = "splitContainer1";
            this.splitContainer1.Orientation = System.Windows.Forms.Orientation.Horizontal;
            // 
            // splitContainer1.Panel1
            // 
            this.splitContainer1.Panel1.Controls.Add(this.labelProgress);
            this.splitContainer1.Panel1.Controls.Add(this.btn_deleteFiles);
            this.splitContainer1.Panel1.Controls.Add(this.chkDryRun);
            this.splitContainer1.Panel1.Controls.Add(this.btn_AnalyzeLevel);
            this.splitContainer1.Panel1.Controls.Add(this.btn_openLevelFolder);
            this.splitContainer1.Panel1.Controls.Add(this.textBox1);
            this.splitContainer1.Panel1.Controls.Add(this.label1);
            // 
            // splitContainer1.Panel2
            // 
            this.splitContainer1.Panel2.Controls.Add(this.labelFileSummary);
            this.splitContainer1.Panel2.Controls.Add(this.cbAllNone);
            this.splitContainer1.Panel2.Controls.Add(this.dataGridViewDeleteList);
            this.splitContainer1.Size = new System.Drawing.Size(1057, 674);
            this.splitContainer1.SplitterDistance = 175;
            this.splitContainer1.TabIndex = 14;
            // 
            // labelProgress
            // 
            this.labelProgress.AutoSize = true;
            this.labelProgress.Location = new System.Drawing.Point(20, 146);
            this.labelProgress.Name = "labelProgress";
            this.labelProgress.Size = new System.Drawing.Size(98, 20);
            this.labelProgress.TabIndex = 19;
            this.labelProgress.Text = "labelProgress";
            // 
            // btn_deleteFiles
            // 
            this.btn_deleteFiles.Location = new System.Drawing.Point(197, 111);
            this.btn_deleteFiles.Name = "btn_deleteFiles";
            this.btn_deleteFiles.Size = new System.Drawing.Size(94, 29);
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
            this.chkDryRun.Location = new System.Drawing.Point(22, 81);
            this.chkDryRun.Name = "chkDryRun";
            this.chkDryRun.Size = new System.Drawing.Size(198, 24);
            this.chkDryRun.TabIndex = 17;
            this.chkDryRun.Text = "Dry Run without Deletion";
            this.chkDryRun.UseVisualStyleBackColor = true;
            // 
            // btn_AnalyzeLevel
            // 
            this.btn_AnalyzeLevel.Location = new System.Drawing.Point(19, 111);
            this.btn_AnalyzeLevel.Name = "btn_AnalyzeLevel";
            this.btn_AnalyzeLevel.Size = new System.Drawing.Size(142, 29);
            this.btn_AnalyzeLevel.TabIndex = 16;
            this.btn_AnalyzeLevel.Text = "Analyze Level";
            this.btn_AnalyzeLevel.UseVisualStyleBackColor = true;
            this.btn_AnalyzeLevel.Click += new System.EventHandler(this.btn_AnalyzeLevel_Click);
            // 
            // btn_openLevelFolder
            // 
            this.btn_openLevelFolder.Location = new System.Drawing.Point(589, 41);
            this.btn_openLevelFolder.Name = "btn_openLevelFolder";
            this.btn_openLevelFolder.Size = new System.Drawing.Size(178, 29);
            this.btn_openLevelFolder.TabIndex = 15;
            this.btn_openLevelFolder.Text = "Select Level Folder";
            this.btn_openLevelFolder.UseVisualStyleBackColor = true;
            this.btn_openLevelFolder.Click += new System.EventHandler(this.btn_openLevelFolder_Click);
            // 
            // textBox1
            // 
            this.textBox1.Location = new System.Drawing.Point(19, 43);
            this.textBox1.Name = "textBox1";
            this.textBox1.Size = new System.Drawing.Size(564, 27);
            this.textBox1.TabIndex = 14;
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(19, 20);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(89, 20);
            this.label1.TabIndex = 13;
            this.label1.Text = "Level Folder";
            // 
            // labelFileSummary
            // 
            this.labelFileSummary.AutoSize = true;
            this.labelFileSummary.Dock = System.Windows.Forms.DockStyle.Right;
            this.labelFileSummary.Location = new System.Drawing.Point(1007, 24);
            this.labelFileSummary.Name = "labelFileSummary";
            this.labelFileSummary.Size = new System.Drawing.Size(50, 20);
            this.labelFileSummary.TabIndex = 20;
            this.labelFileSummary.Text = "label2";
            // 
            // cbAllNone
            // 
            this.cbAllNone.AutoSize = true;
            this.cbAllNone.Checked = true;
            this.cbAllNone.CheckState = System.Windows.Forms.CheckState.Checked;
            this.cbAllNone.Dock = System.Windows.Forms.DockStyle.Top;
            this.cbAllNone.Location = new System.Drawing.Point(0, 0);
            this.cbAllNone.Name = "cbAllNone";
            this.cbAllNone.Size = new System.Drawing.Size(1057, 24);
            this.cbAllNone.TabIndex = 19;
            this.cbAllNone.Text = "Selection All / None";
            this.cbAllNone.UseVisualStyleBackColor = true;
            this.cbAllNone.CheckedChanged += new System.EventHandler(this.cbAllNone_CheckedChanged);
            // 
            // dataGridViewDeleteList
            // 
            this.dataGridViewDeleteList.AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill;
            this.dataGridViewDeleteList.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dataGridViewDeleteList.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dataGridViewDeleteList.Location = new System.Drawing.Point(0, 0);
            this.dataGridViewDeleteList.Name = "dataGridViewDeleteList";
            this.dataGridViewDeleteList.RowHeadersWidth = 51;
            this.dataGridViewDeleteList.RowTemplate.Height = 29;
            this.dataGridViewDeleteList.Size = new System.Drawing.Size(1057, 495);
            this.dataGridViewDeleteList.TabIndex = 0;
            this.dataGridViewDeleteList.CellContentClick += new System.Windows.Forms.DataGridViewCellEventHandler(this.dataGridViewDeleteList_CellContentClick);
            this.dataGridViewDeleteList.CellValueChanged += new System.Windows.Forms.DataGridViewCellEventHandler(this.dataGridViewDeleteList_CellValueChanged);
            this.dataGridViewDeleteList.CurrentCellDirtyStateChanged += new System.EventHandler(this.dataGridViewDeleteList_CurrentCellDirtyStateChanged);
            // 
            // tabPage2
            // 
            this.tabPage2.Controls.Add(this.richTextBoxErrors);
            this.tabPage2.Location = new System.Drawing.Point(4, 29);
            this.tabPage2.Name = "tabPage2";
            this.tabPage2.Padding = new System.Windows.Forms.Padding(3);
            this.tabPage2.Size = new System.Drawing.Size(1063, 680);
            this.tabPage2.TabIndex = 1;
            this.tabPage2.Text = "Errors";
            this.tabPage2.UseVisualStyleBackColor = true;
            // 
            // richTextBoxErrors
            // 
            this.richTextBoxErrors.Dock = System.Windows.Forms.DockStyle.Fill;
            this.richTextBoxErrors.Location = new System.Drawing.Point(3, 3);
            this.richTextBoxErrors.Name = "richTextBoxErrors";
            this.richTextBoxErrors.Size = new System.Drawing.Size(1057, 674);
            this.richTextBoxErrors.TabIndex = 0;
            this.richTextBoxErrors.Text = "";
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 20F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1071, 713);
            this.Controls.Add(this.tabControl1);
            this.Name = "Form1";
            this.Text = "BeamNG Level Cleanup";
            this.tabControl1.ResumeLayout(false);
            this.tabPage1.ResumeLayout(false);
            this.splitContainer1.Panel1.ResumeLayout(false);
            this.splitContainer1.Panel1.PerformLayout();
            this.splitContainer1.Panel2.ResumeLayout(false);
            this.splitContainer1.Panel2.PerformLayout();
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
        private FolderBrowserDialog folderBrowserDialog1;
        private Button btn_deleteFiles;
        private CheckBox cbAllNone;
        private Label labelFileSummary;
        private RichTextBox richTextBoxErrors;
        private Label labelProgress;
    }
}