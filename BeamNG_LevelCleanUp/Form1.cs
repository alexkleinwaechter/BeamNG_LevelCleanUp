using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;
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
        private BindingSource bindingSourceDeleteList = new BindingSource();
        private List<GridFileListItem> SelectedFilesForDeletion { get; set; } = new List<GridFileListItem>();
        private BeamFileReader Reader { get; set; }
        private List<string> _missingFiles { get; set; } = new List<string>();
        public Form1()
        {
            InitializeComponent();
            // Set the help text description for the FolderBrowserDialog.
            this.folderBrowserDialogLevel.Description =
                "Select the directory of your unzipped level.";

            // Do not allow the user to create new files via the FolderBrowserDialog.
            this.folderBrowserDialogLevel.ShowNewFolderButton = false;

            // Default to the My Documents folder.
            this.folderBrowserDialogLevel.RootFolder = Environment.SpecialFolder.Personal;

            this.openFileDialogLog.Filter = "Logfiles (*.log)|*.log";
            this.openFileDialogLog.FileName = "beamng.log";

            labelFileSummary.Text = String.Empty;
            labelProgress.Text = String.Empty;
            CheckVisibility();
        }


        private void btn_openLevelFolder_Click(object sender, EventArgs e)
        {
            // Show the FolderBrowserDialog.
            DialogResult result = folderBrowserDialogLevel.ShowDialog();
            if (result == DialogResult.OK)
            {
                this.textBox1.Text = folderBrowserDialogLevel.SelectedPath;
                CheckVisibility();
            }
            CheckVisibility();
        }

        private void btn_AnalyzeLevel_Click(object sender, EventArgs e)
        {
            SelectedFilesForDeletion = new List<GridFileListItem>();
            try
            {
                richTextBoxErrors.Clear();
                Reader = new BeamFileReader(this.textBox1.Text, this.chkDryRun.Checked);
                Reader.Reset();
                labelProgress.Text = "Read info.json";
                Application.DoEvents();
                Reader.ReadInfoJson();
                labelProgress.Text = "Read Missiongroups";
                Application.DoEvents();
                Reader.ReadMissionGroup();
                labelProgress.Text = "Read Forestfiles";
                Application.DoEvents();
                Reader.ReadForest();
                labelProgress.Text = "Read Decals";
                Application.DoEvents();
                Reader.ReadDecals();
                labelProgress.Text = "Read terrain.json";
                Application.DoEvents();
                Reader.ReadTerrainJson();
                labelProgress.Text = "Read materials.json";
                Application.DoEvents();
                Reader.ReadMaterialsJson();
                labelProgress.Text = "Read all Dae files";
                Application.DoEvents();
                Reader.ReadAllDae();
                labelProgress.Text = "Read Cs files";
                Application.DoEvents();
                Reader.ReadCsFilesForGenericExclude();
                labelProgress.Text = "Resolve unused managed asset files";
                Application.DoEvents();
                Reader.ResolveUnusedAssetFiles();
                labelProgress.Text = "Resolve orphaned unmanaged asset files";
                Application.DoEvents();
                Reader.ResolveOrphanedFiles();
                if (!string.IsNullOrEmpty(this.textBox2.Text))
                {
                    labelProgress.Text = "Analyzing done";
                    Application.DoEvents();
                    _missingFiles = Reader.GetMissingFilesFromBeamLog(this.textBox2.Text);
                }
                labelProgress.Text = "Analyzing done";
                Application.DoEvents();
            }
            catch (Exception ex)
            {
                richTextBoxErrors.Text += Environment.NewLine + labelProgress.Text + ":";
                richTextBoxErrors.Text += Environment.NewLine + ex.Message;
                if (ex.InnerException != null) {
                    richTextBoxErrors.Text += Environment.NewLine + ex.InnerException.Message;
                }
                richTextBoxErrors.Text += Environment.NewLine + "________________________________________";
                this.tabControl1.SelectedTab = tabPage2;
            }
            BuildGridDeleteFiles();
            FillDeleteList();
        }

        private void FillDeleteList()
        {
            SelectedFilesForDeletion = new List<GridFileListItem>();
            foreach (var file in Reader.GetDeleteList())
            {
                var item = new GridFileListItem
                {
                    FileInfo = file,
                    Selected = _missingFiles.Any(x => x.Equals(file.FullName,StringComparison.InvariantCultureIgnoreCase)) ? false : this.cbAllNone.Checked,
                    SizeMb = file.Exists ? Math.Round((file.Length / 1024f) / 1024f, 2) : 0
                };
                SelectedFilesForDeletion.Add(item);
                bindingSourceDeleteList.Add(item);
            }

            dataGridViewDeleteList.DataSource = bindingSourceDeleteList;
            UpdateLabel();
            CheckVisibility();
        }

        private void BuildGridDeleteFiles()
        {
            dataGridViewDeleteList.AutoGenerateColumns = false;
            dataGridViewDeleteList.AutoSize = true;

            DataGridViewCheckBoxColumn col1 = new DataGridViewCheckBoxColumn();
            col1.DataPropertyName = "Selected";
            col1.Name = "Delete";
            dataGridViewDeleteList.Columns.Add(col1);

            DataGridViewColumn col2 = new DataGridViewTextBoxColumn();
            col2.DataPropertyName = "FileInfo";
            col2.Name = "Filename";
            dataGridViewDeleteList.Columns.Add(col2);
            DataGridViewColumn col3 = new DataGridViewTextBoxColumn();
            col3.DataPropertyName = "SizeMb";
            col3.Name = "Filesize Megabytes";
            dataGridViewDeleteList.Columns.Add(col3);
            dataGridViewDeleteList.DataBindingComplete += (o, _) =>
            {
                var dataGridView = o as DataGridView;
                if (dataGridView != null)
                {
                    dataGridView.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
                    dataGridView.Columns[dataGridView.ColumnCount - 1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                }
            };
        }

        private void dataGridViewDeleteList_CellContentClick(object sender,
            DataGridViewCellEventArgs e)
        {
            dataGridViewDeleteList.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        /// <summary>
        /// Works with the above.
        /// </summary>
        private void dataGridViewDeleteList_CellValueChanged(object sender,
            DataGridViewCellEventArgs e)
        {
            dataGridViewDeleteList.CommitEdit(DataGridViewDataErrorContexts.Commit);
            var item = (GridFileListItem)dataGridViewDeleteList.Rows[e.RowIndex].DataBoundItem;
            var listItem = SelectedFilesForDeletion.FirstOrDefault(x => x.FileInfo.FullName == item.FileInfo.FullName);
            if (listItem != null)
            {
                listItem.Selected = item.Selected;
            }
            UpdateLabel();
            CheckVisibility();
        }

        private void dataGridViewDeleteList_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (dataGridViewDeleteList.IsCurrentCellDirty == true && dataGridViewDeleteList.CurrentCell is DataGridViewCheckBoxCell)
            {
                // use BeginInvoke with (MethodInvoker) to run the code after the event is finished
                BeginInvoke((MethodInvoker)delegate
                {
                    dataGridViewDeleteList.EndEdit(DataGridViewDataErrorContexts.Commit);

                });
            }
        }

        private void btn_deleteFiles_Click(object sender, EventArgs e)
        {
            var selected = SelectedFilesForDeletion
                .Where(x => x.Selected)
                .Select(x => x.FileInfo)
                 .ToList();
            Reader.DeleteFilesAndDeploy(selected);
        }

        private void UpdateLabel()
        {
            var text = $"Files: {SelectedFilesForDeletion.Count()}, Selected: {SelectedFilesForDeletion.Where(x => x.Selected == true).Count()}, Sum Filesize MB: {Math.Round(SelectedFilesForDeletion.Where(x => x.Selected == true).Sum(x => x.SizeMb), 2)}";
            this.labelFileSummary.Text = text;
        }

        private void cbAllNone_CheckedChanged(object sender, EventArgs e)
        {
            bindingSourceDeleteList.Clear();
            FillDeleteList();
        }

        private void CheckVisibility()
        {
            if (string.IsNullOrEmpty(this.textBox1.Text))
            {
                this.btn_AnalyzeLevel.Enabled = false;
            }
            else
            {
                this.btn_AnalyzeLevel.Enabled = true;
            }
            if (this.SelectedFilesForDeletion.Where(x => x.Selected).Count() > 0)
            {
                this.btn_deleteFiles.Enabled = true;
            }
            else
            {
                this.btn_deleteFiles.Enabled = false;
            }
        }

        private void btnOpenLog_Click(object sender, EventArgs e)
        {
            DialogResult result = openFileDialogLog.ShowDialog();
            if (result == DialogResult.OK)
            {
                this.textBox2.Text = openFileDialogLog.FileName;
            }
        }
    }
}
