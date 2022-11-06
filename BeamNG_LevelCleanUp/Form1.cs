using BeamNG_LevelCleanUp.Logic;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BeamNG_LevelCleanUp
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
            // Set the help text description for the FolderBrowserDialog.
            this.folderBrowserDialog1.Description =
                "Select the directory of your unzipped level.";

            // Do not allow the user to create new files via the FolderBrowserDialog.
            this.folderBrowserDialog1.ShowNewFolderButton = false;

            // Default to the My Documents folder.
            this.folderBrowserDialog1.RootFolder = Environment.SpecialFolder.Personal;
        }

        private void label1_Click(object sender, EventArgs e)
        {

        }

        private void btn_openLevelFolder_Click(object sender, EventArgs e)
        {
            // Show the FolderBrowserDialog.
            DialogResult result = folderBrowserDialog1.ShowDialog();
            if (result == DialogResult.OK)
            {
                this.textBox1.Text = folderBrowserDialog1.SelectedPath;

            }
        }

        private void btn_AnalyzeLevel_Click(object sender, EventArgs e)
        {
            var reader = new BeamFileReader(this.textBox1.Text);
            reader.ReadMissionGroup();
            reader.ReadDecals();
            reader.ReadMaterialsJson();
            reader.ReadAllDae();
            reader.ResolveObsoleteFiles();
        }
    }
}
