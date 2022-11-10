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
            this.label1 = new System.Windows.Forms.Label();
            this.textBox1 = new System.Windows.Forms.TextBox();
            this.btn_openLevelFolder = new System.Windows.Forms.Button();
            this.btn_AnalyzeLevel = new System.Windows.Forms.Button();
            this.folderBrowserDialog1 = new System.Windows.Forms.FolderBrowserDialog();
            this.chkDryRun = new System.Windows.Forms.CheckBox();
            this.SuspendLayout();
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(14, 18);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(89, 20);
            this.label1.TabIndex = 0;
            this.label1.Text = "Level Folder";
            this.label1.Click += new System.EventHandler(this.label1_Click);
            // 
            // textBox1
            // 
            this.textBox1.Location = new System.Drawing.Point(14, 41);
            this.textBox1.Name = "textBox1";
            this.textBox1.Size = new System.Drawing.Size(564, 27);
            this.textBox1.TabIndex = 1;
            // 
            // btn_openLevelFolder
            // 
            this.btn_openLevelFolder.Location = new System.Drawing.Point(584, 39);
            this.btn_openLevelFolder.Name = "btn_openLevelFolder";
            this.btn_openLevelFolder.Size = new System.Drawing.Size(178, 29);
            this.btn_openLevelFolder.TabIndex = 2;
            this.btn_openLevelFolder.Text = "Select Level Folder";
            this.btn_openLevelFolder.UseVisualStyleBackColor = true;
            this.btn_openLevelFolder.Click += new System.EventHandler(this.btn_openLevelFolder_Click);
            // 
            // btn_AnalyzeLevel
            // 
            this.btn_AnalyzeLevel.Location = new System.Drawing.Point(12, 120);
            this.btn_AnalyzeLevel.Name = "btn_AnalyzeLevel";
            this.btn_AnalyzeLevel.Size = new System.Drawing.Size(142, 29);
            this.btn_AnalyzeLevel.TabIndex = 3;
            this.btn_AnalyzeLevel.Text = "Analyze Level";
            this.btn_AnalyzeLevel.UseVisualStyleBackColor = true;
            this.btn_AnalyzeLevel.Click += new System.EventHandler(this.btn_AnalyzeLevel_Click);
            // 
            // chkDryRun
            // 
            this.chkDryRun.AutoSize = true;
            this.chkDryRun.Checked = true;
            this.chkDryRun.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkDryRun.Location = new System.Drawing.Point(17, 79);
            this.chkDryRun.Name = "chkDryRun";
            this.chkDryRun.Size = new System.Drawing.Size(198, 24);
            this.chkDryRun.TabIndex = 4;
            this.chkDryRun.Text = "Dry Run without Deletion";
            this.chkDryRun.UseVisualStyleBackColor = true;
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 20F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1071, 713);
            this.Controls.Add(this.chkDryRun);
            this.Controls.Add(this.btn_AnalyzeLevel);
            this.Controls.Add(this.btn_openLevelFolder);
            this.Controls.Add(this.textBox1);
            this.Controls.Add(this.label1);
            this.Name = "Form1";
            this.Text = "BeamNG Level Cleanup";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private Label label1;
        private TextBox textBox1;
        private Button btn_openLevelFolder;
        private Button btn_AnalyzeLevel;
        private FolderBrowserDialog folderBrowserDialog1;
        private CheckBox chkDryRun;
    }
}