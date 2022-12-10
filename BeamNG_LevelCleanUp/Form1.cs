using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;
using System.ComponentModel;
using System.Data;
using System.IO.Compression;
using System.Reflection;
using Application = System.Windows.Forms.Application;

namespace BeamNG_LevelCleanUp
{
    public partial class Form1 : Form
    {
        private BindingSource BindingSourceDelete = new BindingSource();
        private List<GridFileListItem> BindingListDelete { get; set; } = new List<GridFileListItem>();
        private BeamFileReader Reader { get; set; }
        private List<string> _missingFiles { get; set; } = new List<string>();
        private Progress<string> _progress = new Progress<string>();
        private CancellationToken _token { get; set; }
        private static string _levelName { get; set; }
        private static string _levelNameCopyFrom { get; set; }
        private static string _levelPathCopyFrom { get; set; }

        public Form1()
        {
            InitializeComponent();
        }

        public Task InitializeAsync(CancellationToken token)
        {
            _token = token;

            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            Text = "BeamNG Tools for Mapbuilders - version";
            Text = $"{Text} {version.Major}.{version.Minor}.{version.Build}";

            this.folderBrowserDialogLevel.Description =
                "Select the directory of your unzipped level.";

            // Do not allow the user to create new files via the FolderBrowserDialog.
            this.folderBrowserDialogLevel.ShowNewFolderButton = false;

            // Default to the My Documents folder.
            this.folderBrowserDialogLevel.RootFolder = Environment.SpecialFolder.Personal;

            this.openFileDialogLog.Filter = "Logfiles (*.log)|*.log";
            this.openFileDialogLog.FileName = "beamng.log";

            this.openFileDialogZip.Filter = "Zipfiles (*.zip)|*.zip";
            this.openFileDialogZip.FileName = String.Empty;

            labelFileSummary.Text = String.Empty;
            tbProgress.Text = String.Empty;

            coboCompressionLevel1.DataSource = Enum.GetValues(typeof(CompressionLevel));

            CheckVisibility();
            BuildGridDeleteFiles();

            _progress.ProgressChanged += (s, message) =>
            {
                if (tbProgress.InvokeRequired)
                {
                    tbProgress.Invoke(new MethodInvoker(() =>
                    {
                        tbProgress.Text = message;
                    }));
                }
                else if (!tbProgress.IsDisposed)
                {
                    tbProgress.Text = message;
                }
            };

            var consumer = Task.Run(async () =>
            {
                while (await PubSubChannel.ch.Reader.WaitToReadAsync())
                {
                    var msg = await PubSubChannel.ch.Reader.ReadAsync();
                    if (!msg.IsError)
                    {
                        SendProgressMessage(msg.Message);
                    }
                    else
                    {
                        if (richTextBoxErrors.InvokeRequired)
                        {
                            richTextBoxErrors.Invoke(new MethodInvoker(() =>
                            {
                                richTextBoxErrors.Text += Environment.NewLine + msg.Message;
                                richTextBoxErrors.Text += Environment.NewLine + "________________________________________";

                            }));
                        }
                        else if (!richTextBoxErrors.IsDisposed)
                        {
                            richTextBoxErrors.Text += Environment.NewLine + msg.Message;
                            richTextBoxErrors.Text += Environment.NewLine + "________________________________________";
                        }
                    }
                }
            });

            return Task.Delay(TimeSpan.FromSeconds(2));
        }

        public void Initialize()
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
        }


        private void btn_openLevelFolder_Click(object sender, EventArgs e)
        {
            // Show the FolderBrowserDialog.
            DialogResult result = folderBrowserDialogLevel.ShowDialog();
            if (result == DialogResult.OK)
            {
                this.tbLevelPath.Text = folderBrowserDialogLevel.SelectedPath;
                CheckVisibility();
            }
            CheckVisibility();
        }

        private async void btn_AnalyzeLevel_Click(object sender, EventArgs e)
        {
            BindingSourceDelete.DataSource = null;
            labelFileSummary.Text = String.Empty;
            btn_AnalyzeLevel.Enabled = false;
            Application.DoEvents();
            var context = TaskScheduler.FromCurrentSynchronizationContext();
            try
            {
                await Task.Run(() =>
                {
                    Reader = new BeamFileReader(this.tbLevelPath.Text, this.tbBeamLogPath.Text);
                    Reader.ReadAll();
                    _missingFiles = Reader.GetMissingFilesFromBeamLog();
                    _levelName = Reader.GetLevelName();
                });
                tb_rename_current_name.Text = _levelName;
                FillDeleteList();
                btn_AnalyzeLevel.Enabled = true;
                PubSubChannel.SendMessage(false, $"Done! Analyzing finished. Please check the logfiles in {Reader.GetLevelPath()}");
            }
            catch (Exception ex)
            {
                ShowException(ex);
                btn_AnalyzeLevel.Enabled = true;
            }
        }

        private void ShowException(Exception ex)
        {
            richTextBoxErrors.Text += Environment.NewLine + ex.Message;
            if (ex.InnerException != null)
            {
                richTextBoxErrors.Text += Environment.NewLine + ex.InnerException.Message;
            }
            richTextBoxErrors.Text += Environment.NewLine + "________________________________________";
            this.tabControl1.SelectedTab = tabPage2;
        }

        private void FillDeleteList()
        {
            foreach (var file in Reader.GetDeleteList())
            {
                var item = new GridFileListItem
                {
                    FullName = file.FullName,
                    Selected = _missingFiles.Any(x => x.Equals(file.FullName, StringComparison.OrdinalIgnoreCase)) ? false : this.cbAllNoneDeleteList.Checked,
                    SizeMb = file.Exists ? Math.Round((file.Length / 1024f) / 1024f, 2) : 0
                };
                BindingListDelete.Add(item);
            }

            BindingSourceDelete.DataSource = BindingListDelete;
            dataGridViewDeleteList.DataSource = BindingSourceDelete;
            UpdateLabel();
            CheckVisibility();
        }

        private void BuildGridDeleteFiles()
        {
            dataGridViewDeleteList.AutoGenerateColumns = false;
            //dataGridViewDeleteList.AutoSize = true;

            DataGridViewCheckBoxColumn col1 = new DataGridViewCheckBoxColumn();
            col1.DataPropertyName = "Selected";
            col1.Name = "Delete";
            dataGridViewDeleteList.Columns.Add(col1);

            DataGridViewColumn col2 = new DataGridViewTextBoxColumn();
            col2.DataPropertyName = "FullName";
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
                    dataGridView.Dock = DockStyle.Fill;
                    dataGridView.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
                    dataGridView.Columns[dataGridView.ColumnCount - 1].AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
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
            var listItem = BindingListDelete.FirstOrDefault(x => x.FullName == item.FullName);
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
            var selected = BindingListDelete
                .Where(x => x.Selected)
                .Select(x => new FileInfo(x.FullName))
                 .ToList();
            Reader.DeleteFilesAndDeploy(selected, chkDryRun.Checked);
        }

        private void UpdateLabel()
        {
            var text = $"Files: {BindingListDelete.Count()}, Selected: {BindingListDelete.Where(x => x.Selected == true).Count()}, Sum Filesize MB: {Math.Round(BindingListDelete.Where(x => x.Selected == true).Sum(x => x.SizeMb), 2)}";
            this.labelFileSummary.Text = text;
        }

        private void cbAllNoneDeleteList_CheckedChanged(object sender, EventArgs e)
        {
            var filteredBindingList = new BindingList<GridFileListItem>(BindingListDelete.Where(x => x.FullName.ToUpperInvariant().Contains(tbFilterGridDeleteList.Text.ToUpperInvariant())).ToList());
            foreach (var item in filteredBindingList)
            {
                item.Selected = cbAllNoneDeleteList.Checked;
                var selectedListItem = BindingListDelete.SingleOrDefault(x => x.FullName.Equals(item.FullName));
                if (selectedListItem != null)
                {
                    selectedListItem.Selected = cbAllNoneDeleteList.Checked;
                }
            }

            BindingSourceDelete.DataSource = filteredBindingList;
            dataGridViewDeleteList.DataSource = BindingSourceDelete;
            UpdateLabel();
        }

        private void CheckVisibility()
        {
            if (string.IsNullOrEmpty(this.tbLevelPath.Text))
            {
                this.btn_AnalyzeLevel.Enabled = false;
            }
            else
            {
                this.btn_AnalyzeLevel.Enabled = true;
            }
            if (this.BindingListDelete.Where(x => x.Selected).Count() > 0)
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
                this.tbBeamLogPath.Text = openFileDialogLog.FileName;
            }
        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {
            CheckVisibility();
        }

        private void SendProgressMessage(string text)
        {
            ((IProgress<string>)_progress).Report($"{text}");
        }

        private async void btnLoadLevelZipFile_Click(object sender, EventArgs e)
        {
            try
            {
                var path = string.Empty;
                DialogResult result = openFileDialogZip.ShowDialog();
                if (result == DialogResult.OK)
                {
                    tbLevelZipFile.Text = openFileDialogZip.FileName;
                    await Task.Run(() =>
                    {
                        path = ZipFileHandler.ExtractToDirectory(openFileDialogZip.FileName, "_unpacked");
                    });
                    tbLevelPath.Text = path;
                }
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
        }

        private async void btnZipDeployment_Click(object sender, EventArgs e)
        {
            await ZipDeploymentFile();
        }

        private async void btnZipDeployment2_Click(object sender, EventArgs e)
        {
            await ZipDeploymentFile();
        }

        private async Task ZipDeploymentFile()
        {
            Enum.TryParse(coboCompressionLevel1.Text, out CompressionLevel compressionLevel);

            try
            {
                var path = string.Empty;
                if (!string.IsNullOrEmpty(tbLevelZipFile.Text))
                {
                    path = ZipFileHandler.GetLastUnpackedPath();
                    await Task.Run(() =>
                    {
                        ZipFileHandler.BuildDeploymentFile(path, _levelName, compressionLevel);
                    });
                }
                else if (!string.IsNullOrEmpty(tbLevelPath.Text))
                {
                    path = tbLevelPath.Text;
                    await Task.Run(() =>
                    {
                        ZipFileHandler.BuildDeploymentFile(path, _levelName, compressionLevel, true);
                    });
                }
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
        }

        private async void BtnGetCurrentName_Click(object sender, EventArgs e)
        {
            try
            {
                await Task.Run(() =>
                {
                    Reader = new BeamFileReader(this.tbLevelPath.Text, this.tbBeamLogPath.Text);
                    _levelName = Reader.GetLevelName();
                });
                tb_rename_current_name.Text = _levelName;
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
        }

        private async void Btn_RenameLevel_Click(object sender, EventArgs e)
        {
            try
            {
                await Task.Run(() =>
                {
                    Reader = new BeamFileReader(this.tbLevelPath.Text, this.tbBeamLogPath.Text);
                    Reader.RenameLevel(tb_rename_new_name_path.Text.Replace(" ", "").Trim(), tb_rename_new_name_title.Text);
                    _levelName = Reader.GetLevelName();
                });
                tb_rename_current_name.Text = _levelName;
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
        }

        private void tbFilterGridDeleteList_TextChanged(object sender, EventArgs e)
        {
            var filteredBindingList = new BindingList<GridFileListItem>(BindingListDelete.Where(x => x.FullName.ToUpperInvariant().Contains(tbFilterGridDeleteList.Text.ToUpperInvariant())).ToList());
            BindingSourceDelete.DataSource = filteredBindingList;
            dataGridViewDeleteList.DataSource = BindingSourceDelete;
        }

        private async void BtnCopyFromZipLevel_Click(object sender, EventArgs e)
        {
            try
            {
                var path = string.Empty;
                DialogResult result = openFileDialogZip.ShowDialog();
                if (result == DialogResult.OK)
                {
                    tbCopyFromLevel.Text = openFileDialogZip.FileName;
                    await Task.Run(() =>
                    {
                        _levelPathCopyFrom = ZipFileHandler.ExtractToDirectory(openFileDialogZip.FileName, "_copyFrom");
                    });
                    await Task.Run(() =>
                    {
                        Reader = new BeamFileReader(_levelPathCopyFrom, null);
                        _levelNameCopyFrom = Reader.GetLevelName();
                    });
                    lbLevelNameCopyFrom.Text = _levelNameCopyFrom;
                }
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
        }

        private async void BtnScanAssets_Click(object sender, EventArgs e)
        {
            await Task.Run(() =>
            {
                Reader = new BeamFileReader(this.tbLevelPath.Text, this.tbBeamLogPath.Text, _levelPathCopyFrom);
                Reader.ReadAllForCopy();
            });
        }
    }
}
