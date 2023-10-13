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
            folderBrowserDialogLevel = new FolderBrowserDialog();
            openFileDialogLog = new OpenFileDialog();
            openFileDialogZip = new OpenFileDialog();
            backgroundWorker1 = new System.ComponentModel.BackgroundWorker();
            blazorWebView1 = new Microsoft.AspNetCore.Components.WebView.WindowsForms.BlazorWebView();
            SuspendLayout();
            // 
            // blazorWebView1
            // 
            blazorWebView1.Dock = DockStyle.Fill;
            blazorWebView1.Location = new System.Drawing.Point(0, 0);
            blazorWebView1.Margin = new Padding(4);
            blazorWebView1.Name = "blazorWebView1";
            blazorWebView1.Size = new System.Drawing.Size(2523, 1430);
            blazorWebView1.TabIndex = 1;
            blazorWebView1.Text = "blazorWebView1";
            // 
            // Form1
            // 
            AutoScaleDimensions = new System.Drawing.SizeF(12F, 30F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new System.Drawing.Size(2523, 1430);
            Controls.Add(blazorWebView1);
            Margin = new Padding(4);
            Name = "Form1";
            StartPosition = FormStartPosition.CenterScreen;
            FormClosing += Form1_FormClosing;
            FormClosed += Form1_FormClosed;
            ResumeLayout(false);
        }

        #endregion
        private FolderBrowserDialog folderBrowserDialogLevel;
        private OpenFileDialog openFileDialogLog;
        private OpenFileDialog openFileDialogZip;
        private System.ComponentModel.BackgroundWorker backgroundWorker1;
        private Microsoft.AspNetCore.Components.WebView.WindowsForms.BlazorWebView blazorWebView1;
    }
}