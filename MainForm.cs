namespace StartupManager;

using System.ComponentModel;
using System.Text;
using StartupManager.Models;
using StartupManager.Services;

/// <summary>主窗口：启动项列表 + 过滤 + 启用/禁用。</summary>
public sealed class MainForm : Form
{
    private readonly StartupService _service = new();

    private readonly DataGridView _grid;
    private readonly ToolStrip _toolStrip;
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripStatusLabel _countLabel;
    private readonly CheckBox _showSystem;
    private readonly TextBox _searchBox;
    private readonly ComboBox _sourceFilter;

    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _miToggle;
    private readonly ToolStripMenuItem _miOpen;
    private readonly ToolStripMenuItem _miCopy;
    private readonly ToolStripMenuItem _miCopyPath;
    private readonly ToolStripMenuItem _miDetails;

    private ScanResult _result = new();
    private bool _busy;

    public MainForm()
    {
        Text = "启动项管理器 — 识别并管理非系统开机启动项";
        Width = 1180;
        Height = 720;
        MinimumSize = new Size(900, 500);
        StartPosition = FormStartPosition.CenterScreen;

        // ── 顶部工具条 ──
        _toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };

        var btnScan = new ToolStripButton("重新扫描") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnScan.Click += async (_, _) => await ScanAsync();

        var btnToggle = new ToolStripButton("启用/禁用选中项") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnToggle.Click += async (_, _) => await ToggleSelectedAsync();

        var btnOpenLocation = new ToolStripButton("打开文件位置") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnOpenLocation.Click += (_, _) => OpenFileLocation();

        var btnCopyCmd = new ToolStripButton("复制切换命令") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnCopyCmd.Click += (_, _) => CopyCommand();

        var btnExport = new ToolStripButton("导出表格") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnExport.Click += (_, _) => ExportCsv();

        _toolStrip.Items.Add(btnScan);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(btnToggle);
        _toolStrip.Items.Add(btnOpenLocation);
        _toolStrip.Items.Add(btnCopyCmd);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(btnExport);

        // ── 右键上下文菜单 ──
        // 与顶部工具条功能一一对应，便于在列表上就地操作。
        _contextMenu = new ContextMenuStrip();

        var miToggle = new ToolStripMenuItem("启用 / 禁用该项");
        miToggle.Click += async (_, _) => await ToggleSelectedAsync();

        var miOpen = new ToolStripMenuItem("打开文件位置");
        miOpen.Click += (_, _) => OpenFileLocation();

        var miCopy = new ToolStripMenuItem("复制切换命令");
        miCopy.Click += (_, _) => CopyCommand();

        var miCopyPath = new ToolStripMenuItem("复制目标路径");
        miCopyPath.Click += (_, _) => CopyTargetPath();

        var miDetails = new ToolStripMenuItem("查看详细信息");
        miDetails.Click += (_, _) => ShowDetails();

        var miScan = new ToolStripMenuItem("重新扫描");
        miScan.Click += async (_, _) => await ScanAsync();

        var miExport = new ToolStripMenuItem("导出表格");
        miExport.Click += (_, _) => ExportCsv();

        _contextMenu.Items.Add(miToggle);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(miOpen);
        _contextMenu.Items.Add(miCopyPath);
        _contextMenu.Items.Add(miCopy);
        _contextMenu.Items.Add(miDetails);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(miScan);
        _contextMenu.Items.Add(miExport);

        // 保留引用，便于按选中项灰显某些菜单项
        _miToggle = miToggle;
        _miOpen = miOpen;
        _miCopy = miCopy;
        _miCopyPath = miCopyPath;
        _miDetails = miDetails;

        _contextMenu.Opening += (_, _) =>
        {
            bool has = SelectedItem is not null;
            _miToggle.Enabled = has;
            _miOpen.Enabled = has;
            _miCopy.Enabled = has;
            _miCopyPath.Enabled = has;
            _miDetails.Enabled = has;
        };

        // ── 过滤栏 ──
        var filterPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(8, 6, 8, 0),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        filterPanel.Controls.Add(new Label
        {
            Text = "搜索：",
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0),
        });

        _searchBox = new TextBox { Width = 220 };
        _searchBox.TextChanged += (_, _) => ApplyFilter();
        filterPanel.Controls.Add(_searchBox);

        filterPanel.Controls.Add(new Label
        {
            Text = "  来源：",
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0),
        });

        _sourceFilter = new ComboBox
        {
            Width = 170,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _sourceFilter.Items.Add("全部来源");
        foreach (StartupSource s in Enum.GetValues<StartupSource>())
        {
            _sourceFilter.Items.Add(new SourceItem(s));
        }
        _sourceFilter.SelectedIndex = 0;
        _sourceFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        filterPanel.Controls.Add(_sourceFilter);

        _showSystem = new CheckBox
        {
            Text = "  显示系统项",
            AutoSize = true,
            Padding = new Padding(10, 5, 0, 0),
        };
        _showSystem.CheckedChanged += (_, _) => ApplyFilter();
        filterPanel.Controls.Add(_showSystem);

        // ── 数据网格 ──
        _grid = new DataGridView
        {
            // 关键：必须关闭自动生成列。
            // 否则 DataGridView 会把 StartupItem 的所有公开属性
            // （Source、ExecutablePath、Exists、IsSystem、Locator、CanToggle …）
            // 全部自动加成一列，表头就会出现一堆英文属性名。
            // 本程序只展示下方显式添加的 6 列。
            AutoGenerateColumns = false,
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,

            // 列宽按 FillWeight 比例自动填满整个可视宽度：
            // 窗口变宽时列会同步变宽，不会在右侧留出大片空白；
            // 也不会露出 DataGridView 默认的 AppWorkspace 灰色底。
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,

            // 行数不足以填满可见区域时，下方空白用白色，与行背景融为一体。
            // 默认值是 SystemColors.AppWorkspace（一块明显的灰），很难看。
            BackgroundColor = Color.White,

            // 去掉单元格之间那条灰色的网格线，整体更干净
            GridColor = Color.FromArgb(230, 233, 237),
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,

            RowHeadersVisible = false,
            EnableHeadersVisualStyles = false,
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(248, 249, 250),
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(240, 242, 245),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = new Font("Microsoft YaHei UI", 9F),
            },
            RowTemplate = { Height = 24 },
        };

        // ── 列定义 ──
        // AutoSizeColumnsMode=Fill 时列宽由 FillWeight 决定相对比例：
        //   "命令行/目标"是最需要横向空间的一列，给它最大权重；
        //   "状态"最窄。
        // 这样无论窗口多宽，所有列都会按比例撑满，不留空白。

        // 状态列：绑定 bool 属性，但显示为中文。
        // 设为 Programmatic 排序，避免默认按布尔值排序与中文显示不一致。
        var statusCol = MakeColumn("Enabled", "状态", 60, fillWeight: 7);
        statusCol.SortMode = DataGridViewColumnSortMode.Programmatic;
        _grid.Columns.Add(statusCol);

        _grid.Columns.Add(MakeColumn("Name", "名称", 240, fillWeight: 24));
        _grid.Columns.Add(MakeColumn("SourceText", "来源", 130, fillWeight: 14));
        _grid.Columns.Add(MakeColumn("Command", "命令行 / 目标", 520, fillWeight: 42));
        _grid.Columns.Add(MakeColumn("Publisher", "发布者", 150, fillWeight: 15));
        _grid.Columns.Add(MakeColumn("Note", "备注", 200, fillWeight: 18));

        // 允许用户拖动列宽：Fill 模式下拖完会按新比例重新分配剩余空间
        _grid.AllowUserToResizeColumns = true;

        // 状态列着色：禁用项显示为灰色
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count)
            {
                return;
            }

            if (_grid.Rows[e.RowIndex].Tag is StartupItem item)
            {
                // "状态"列绑定的是 bool 属性，不能直接把单元格值改成中文
                // （改成字符串会导致 DataGridView 回写数据源时抛 FormatException）。
                // 正确做法：只覆盖显示用的 FormattedValue，不动底层值。
                if (_grid.Columns[e.ColumnIndex].Name == "Enabled")
                {
                    e.Value = item.Enabled ? "启用" : "已禁用";
                    e.FormattingApplied = true;
                }

                if (!item.Enabled)
                {
                    e.CellStyle!.ForeColor = Color.Gray;
                }
                else if (!item.Exists)
                {
                    e.CellStyle!.ForeColor = Color.FromArgb(180, 90, 0);
                }
            }
        };

        // 兜底：即使将来出现意外的数据转换问题，也不要弹出令人困惑的
        // "DataGridView 默认错误"对话框，而是静默忽略并在状态栏提示。
        _grid.DataError += (_, e) =>
        {
            e.ThrowException = false;
            string msg = e.Exception?.Message ?? "未知的格式错误";
            if (_statusLabel is not null)
            {
                _statusLabel.Text = "界面显示出现异常（已忽略）：" + msg;
            }
        };

        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                _ = ToggleSelectedAsync();
            }
        };

        // 右键：先选中鼠标所在行，再弹出上下文菜单
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0)
            {
                return;
            }

            _grid.ClearSelection();
            _grid.Rows[e.RowIndex].Selected = true;
            _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[Math.Max(e.ColumnIndex, 0)];
        };

        _grid.ContextMenuStrip = _contextMenu;
        _grid.ShowCellToolTips = true;

        // ── 状态栏 ──
        _statusStrip = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel("就绪") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _countLabel = new ToolStripStatusLabel("");
        _statusStrip.Items.Add(_statusLabel);
        _statusStrip.Items.Add(_countLabel);

        // ── 布局 ──
        // 不用裸 Dock 堆叠（容易因 z-order 顺序出错导致控件重叠/溢出），
        // 改用 TableLayoutPanel 显式划分行：
        //   行0 = 工具条（固定高）
        //   行1 = 过滤栏（固定高）
        //   行2 = 网格（100% 填充，随窗口伸缩）
        //   行3 = 状态栏（固定高）
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 工具条
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F)); // 过滤栏
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // 网格：占据全部剩余空间
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 状态栏

        // Dock=Fill 让它们各自填满所属单元格
        _toolStrip.Dock = DockStyle.Fill;
        filterPanel.Dock = DockStyle.Fill;
        _grid.Dock = DockStyle.Fill;
        _statusStrip.Dock = DockStyle.Fill;

        layout.Controls.Add(_toolStrip, 0, 0);
        layout.Controls.Add(filterPanel, 0, 1);
        layout.Controls.Add(_grid, 0, 2);
        layout.Controls.Add(_statusStrip, 0, 3);

        Controls.Add(layout);

        Load += async (_, _) => await ScanAsync();
    }

    /// <summary>
    /// 创建一列。
    /// width 作为 Fill 模式的"最小可读宽度"参考值（MinimumWidth），
    /// fillWeight 决定该列在可用宽度中占的比例。
    /// </summary>
    private static DataGridViewTextBoxColumn MakeColumn(
        string prop, string header, int width, int fillWeight) => new()
    {
        DataPropertyName = prop,
        Name = prop,
        HeaderText = header,
        // Fill 模式下 Width 会被自动计算，这里设一个下限防止被压得过窄
        MinimumWidth = Math.Min(width, 90),
        FillWeight = fillWeight,
        SortMode = DataGridViewColumnSortMode.Automatic,
    };

    // ─────────────────────────────────────────────
    // 扫描
    // ─────────────────────────────────────────────

    private async Task ScanAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _statusLabel.Text = "正在扫描启动项 …";
        _grid.Enabled = false;

        try
        {
            var progress = new Progress<string>(msg => _statusLabel.Text = msg);
            _result = await Task.Run(() => _service.ScanAll(progress));
            ApplyFilter();

            _countLabel.Text = $"共 {_result.TotalCount} 项 · 启用 {_result.EnabledCount} · " +
                               $"系统 {_result.SystemCount} · 失效 {_result.MissingCount} · " +
                               $"耗时 {_result.Elapsed.TotalSeconds:0.0}s";

            if (_result.Warnings.Count > 0)
            {
                _statusLabel.Text = "扫描完成，但有警告：" + string.Join("；", _result.Warnings);
            }
            else
            {
                _statusLabel.Text = "扫描完成";
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "扫描失败：" + ex.Message;
            MessageBox.Show(this, "扫描出错：\n\n" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _grid.Enabled = true;
            _busy = false;
        }
    }

    private void ApplyFilter()
    {
        string search = _searchBox.Text.Trim();
        bool showSystem = _showSystem.Checked;
        StartupSource? srcFilter = _sourceFilter.SelectedItem is SourceItem si ? si.Source : null;

        var view = _result.Items.AsEnumerable();

        if (!showSystem)
        {
            view = view.Where(i => !i.IsSystem);
        }

        if (srcFilter.HasValue)
        {
            view = view.Where(i => i.Source == srcFilter.Value);
        }

        if (!string.IsNullOrEmpty(search))
        {
            view = view.Where(i =>
                i.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                i.Command.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                i.Publisher.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                i.SourceText.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var list = view.ToList();
        _grid.DataSource = new BindingList<StartupItem>(list);

        // 用 Tag 保存原始对象，便于格式化与操作
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.DataBoundItem is StartupItem it)
            {
                row.Tag = it;
            }
        }
    }

    // ─────────────────────────────────────────────
    // 操作
    // ─────────────────────────────────────────────

    private StartupItem? SelectedItem =>
        _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as StartupItem : null;

    private async Task ToggleSelectedAsync()
    {
        var item = SelectedItem;
        if (item is null)
        {
            return;
        }

        if (!item.CanToggle)
        {
            MessageBox.Show(this,
                $"「{item.Name}」属于只读来源（{item.SourceText}）。\n\n" +
                "为保证系统安全，本程序不直接修改此类项目。\n" +
                "可使用「复制切换命令」获取手工操作方式。",
                "只读项", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        bool target = !item.Enabled;
        string action = target ? "启用" : "禁用";

        var confirm = MessageBox.Show(this,
            $"确定要{action}以下启动项吗？\n\n" +
            $"名称：{item.Name}\n" +
            $"来源：{item.SourceText}\n" +
            $"目标：{item.Command}\n\n" +
            (target ? "启用后，该项将在下次开机时自动运行。" : "禁用不会删除该项，可随时在本程序中恢复。"),
            $"确认{action}",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        _statusLabel.Text = $"正在{action}：{item.Name} …";

        string? err = await Task.Run(() => _service.SetEnabled(item, target));

        // 权限不足时：自动请求提权，由提权后的新实例完成这项操作
        if (err is not null && ElevationHelper.IsPermissionError(err) && !ElevationHelper.IsAdmin())
        {
            var ask = MessageBox.Show(this,
                $"「{item.Name}」位于需要管理员权限的位置（{item.SourceText}）。\n\n" +
                $"是否以管理员身份重新启动本程序来完成这次{action}？\n" +
                "（会弹出 UAC 确认框，完成后可继续在新窗口中操作）",
                "需要管理员权限",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (ask == DialogResult.Yes)
            {
                var op = new StartupItemRef
                {
                    Source = (int)item.Source,
                    Locator = item.Locator,
                    Note = item.Note,
                    Enable = target,
                };

                string? relaunchErr = ElevationHelper.RelaunchElevatedForOperation(op);
                if (relaunchErr is null)
                {
                    _statusLabel.Text = "已请求管理员权限，请在新窗口中确认";
                    return;
                }

                err = relaunchErr;
            }
        }

        if (err is null)
        {
            item.Enabled = target;
            ApplyFilter();
            _statusLabel.Text = $"已{action}：{item.Name}";
            _countLabel.Text = $"共 {_result.TotalCount} 项 · 启用 {_result.EnabledCount} · " +
                               $"系统 {_result.SystemCount} · 失效 {_result.MissingCount}";
        }
        else
        {
            _statusLabel.Text = $"{action}失败";
            MessageBox.Show(this, $"{action}失败：\n\n{err}", "操作失败",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenFileLocation()
    {
        var item = SelectedItem;
        if (item is null)
        {
            return;
        }

        string? path = item.ExecutablePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "无法定位目标文件：\n" + (path ?? item.Command),
                "文件不存在", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开失败：" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 生成"与当前状态相反"的可复制命令。
    /// 抽成静态方法便于自检覆盖，避免再次出现"命令与意图无关"的错误。
    /// </summary>
    internal static string BuildToggleCommand(StartupItem item)
    {
        bool toDisable = item.Enabled;

        return item.Source switch
        {
            // 服务名必须加引号：很多服务名本身含空格（例如 "Red Giant Service"、
            // "NVIDIA LocalSystem Container"），不加引号 sc 会把名字拆成多个参数，
            // 直接报 usage 错误。
            StartupSource.Service =>
                toDisable
                    ? $"sc config \"{item.Locator}\" start= demand"
                    : $"sc config \"{item.Locator}\" start= auto",

            // 计划任务路径同样可能含空格，已有引号，这里保持
            StartupSource.ScheduledTask =>
                toDisable
                    ? $"schtasks /Change /TN \"{item.Locator}\" /DISABLE"
                    : $"schtasks /Change /TN \"{item.Locator}\" /ENABLE",

            StartupSource.Other =>
                "该来源需要手动在 WMI 中操作，详见界面提示。",

            _ => item.Command,
        };
    }

    private void CopyCommand()
    {
        var item = SelectedItem;
        if (item is null)
        {
            return;
        }

        string text = BuildToggleCommand(item);

        try
        {
            Clipboard.SetText(text);
            _statusLabel.Text = "已复制到剪贴板";
        }
        catch
        {
            MessageBox.Show(this, text, "命令内容",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /// <summary>复制该启动项的目标路径（便于粘贴到资源管理器或命令行）。</summary>
    private void CopyTargetPath()
    {
        var item = SelectedItem;
        if (item is null)
        {
            return;
        }

        string text = item.ExecutablePath ?? item.Command ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
            _statusLabel.Text = "已复制目标路径";
        }
        catch
        {
            MessageBox.Show(this, text, "目标路径",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /// <summary>显示该项的完整信息（右键菜单用，便于排查）。</summary>
    private void ShowDetails()
    {
        var item = SelectedItem;
        if (item is null)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"名称：{item.Name}");
        sb.AppendLine($"来源：{item.SourceText}");
        sb.AppendLine($"状态：{(item.Enabled ? "启用" : "已禁用")}");
        sb.AppendLine();
        sb.AppendLine("命令行 / 目标：");
        sb.AppendLine(item.Command);
        sb.AppendLine();
        sb.AppendLine($"可执行文件：{item.ExecutablePath ?? "（无法解析）"}");
        sb.AppendLine($"文件存在：{(item.Exists ? "是" : "否（失效项）")}");
        sb.AppendLine($"发布者：{(string.IsNullOrEmpty(item.Publisher) ? "（未知）" : item.Publisher)}");
        sb.AppendLine($"可修改：{(item.CanToggle ? "是" : "否（只读来源）")}");
        sb.AppendLine();
        sb.AppendLine("定位标识：");
        sb.AppendLine(item.Locator);
        if (!string.IsNullOrEmpty(item.Note))
        {
            sb.AppendLine();
            sb.AppendLine($"备注：{item.Note}");
        }

        MessageBox.Show(this, sb.ToString(), "启动项详细信息",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExportCsv()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"startup_items_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };

        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("状态,名称,来源,命令行,发布者,文件存在,备注");

            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is not StartupItem it)
                {
                    continue;
                }

                sb.AppendLine(string.Join(",",
                    Csv(it.Enabled ? "启用" : "已禁用"),
                    Csv(it.Name),
                    Csv(it.SourceText),
                    Csv(it.Command),
                    Csv(it.Publisher),
                    Csv(it.Exists ? "是" : "否"),
                    Csv(it.Note)));
            }

            File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
            _statusLabel.Text = "已导出：" + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string Csv(string v)
    {
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
        {
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }
        return v;
    }

    /// <summary>来源下拉项包装，用于显示友好名称。</summary>
    private sealed class SourceItem
    {
        public StartupSource Source { get; }

        public SourceItem(StartupSource s) => Source = s;

        public override string ToString() => new StartupItem { Source = Source }.SourceText;
    }
}
