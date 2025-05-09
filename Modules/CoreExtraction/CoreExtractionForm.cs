using Krypton.Toolkit;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS
{
    /// <summary>Interactive core-extraction tool with support for selecting diameter view and orientation.</summary>
    public sealed class CoreExtractionForm : KryptonForm
    {
        #region ========== Fields ==========
        private readonly MainForm _main;
        private readonly int _w, _h, _d;                // volume sizes

        // three orthogonal views
        private PictureBox _xyPic, _xzPic, _yzPic;
        private TrackBar _xyTrack, _xzTrack, _yzTrack;
        private NumericUpDown _xyNum, _xzNum, _yzNum;
        private Label _xyLbl, _xzLbl, _yzLbl;
        private TableLayoutPanel _layout;
        private int _xySlice, _xzSlice, _yzSlice;

        // cylinder (core) parameters
        private Point3D _center;
        private float _diameter, _length;
        private bool _showCore = true;
        private bool _isVertical = true;  // Core orientation (vertical or horizontal)
        private View _diameterView = View.XY;  // View to use for diameter selection

        // UI controls for core properties
        private NumericUpDown _numDiameter, _numLength;
        private ComboBox _cmbOrientation, _cmbDiameterView;

        // slice caches (use LRUCache from your implementation)
        private readonly LRUCache<int, Bitmap> _xyCache, _xzCache, _yzCache;
        private CancellationTokenSource _renderCts = new CancellationTokenSource();

        // mouse interaction
        private bool _dragCenter, _resize;
        private Point _lastMouseScreen;
        private View _activeView = View.XY;

        // layout cycling
        private int _currentLayoutIndex;
        private const int CACHE_SIZE = 24;

        private enum View { XY, XZ, YZ }
        #endregion

        #region ========== Ctor / init ==========
        public CoreExtractionForm(MainForm host)
        {
            _main = host ?? throw new ArgumentNullException(nameof(host));
            _w = host.GetWidth();
            _h = host.GetHeight();
            _d = host.GetDepth();

            _center = new Point3D(_w / 2f, _h / 2f, _d / 2f);
            _diameter = Math.Min(_w, _h) / 2f;      // Full diameter (not radius)
            _length = _d * 0.8f;

            _xyCache = new LRUCache<int, Bitmap>(CACHE_SIZE);
            _xzCache = new LRUCache<int, Bitmap>(CACHE_SIZE);
            _yzCache = new LRUCache<int, Bitmap>(CACHE_SIZE);

            InitializeComponent();
            InitSlicePositions();
            RenderAllAsync();                     // fire-and-forget
        }

        private void InitializeComponent()
        {
            Text = "Core Extraction";
            Size = new Size(1200, 960);
            MinimumSize = new Size(1000, 800);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(45, 45, 48);
            Icon = Properties.Resources.favicon;
            AutoScroll = true; // Make the entire form scrollable if needed

            // ---- master layout -----------------------------------------------------------
            _layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 2,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
                Margin = new Padding(0),
                AutoScroll = true
            };
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 88f));
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 12f));
            Controls.Add(_layout);

            // ---- three view panels -------------------------------------------------------
            Panel xyPanel, xzPanel, yzPanel;
            CreateViewPanel("XY View", View.XY, out xyPanel, out _xyPic, out _xyTrack, out _xyNum, out _xyLbl);
            CreateViewPanel("XZ View", View.XZ, out xzPanel, out _xzPic, out _xzTrack, out _xzNum, out _xzLbl);
            CreateViewPanel("YZ View", View.YZ, out yzPanel, out _yzPic, out _yzTrack, out _yzNum, out _yzLbl);

            _layout.Controls.Add(xyPanel, 0, 0);
            _layout.Controls.Add(xzPanel, 1, 0);
            _layout.Controls.Add(yzPanel, 2, 0);

            // ---- bottom control row ------------------------------------------------------
            var ctlPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };
            _layout.Controls.Add(ctlPanel, 0, 1);
            _layout.SetColumnSpan(ctlPanel, 3);

            // Using TableLayoutPanel for the bottom controls to ensure proper alignment
            var bottomLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
                Margin = new Padding(0)
            };
            bottomLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35f));
            bottomLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65f));
            ctlPanel.Controls.Add(bottomLayout);

            // Core Properties group - left panel
            var coreGroup = new GroupBox
            {
                Text = "Core Properties",
                ForeColor = Color.White,
                BackColor = Color.FromArgb(60, 60, 65),
                Dock = DockStyle.Fill,
                Padding = new Padding(5),
                Margin = new Padding(5)
            };
            bottomLayout.Controls.Add(coreGroup, 0, 0);

            // Add a scrollable panel inside the group box
            var coreScrollPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.Transparent
            };
            coreGroup.Controls.Add(coreScrollPanel);

            // Core properties layout - using absolute positioning for reliable layout with scrolling
            var propsPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 150, // Make this panel tall enough for all controls
                BackColor = Color.Transparent
            };
            coreScrollPanel.Controls.Add(propsPanel);

            // Diameter label and control
            propsPanel.Controls.Add(new Label
            {
                Text = "Diameter (px):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 15)
            });

            _numDiameter = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 3000,
                Value = (decimal)_diameter,
                BackColor = Color.FromArgb(80, 80, 85),
                ForeColor = Color.White,
                Location = new Point(110, 13),
                Width = 80,
                BorderStyle = BorderStyle.FixedSingle
            };
            _numDiameter.ValueChanged += (s, e) => { _diameter = (float)_numDiameter.Value; RefreshAll(); };
            propsPanel.Controls.Add(_numDiameter);

            // Length label and control
            propsPanel.Controls.Add(new Label
            {
                Text = "Length (px):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(200, 15)
            });

            _numLength = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 4000,
                Value = (decimal)_length,
                BackColor = Color.FromArgb(80, 80, 85),
                ForeColor = Color.White,
                Location = new Point(280, 13),
                Width = 80,
                BorderStyle = BorderStyle.FixedSingle
            };
            _numLength.ValueChanged += (s, e) => { _length = (float)_numLength.Value; RefreshAll(); };
            propsPanel.Controls.Add(_numLength);

            // Orientation label and control
            propsPanel.Controls.Add(new Label
            {
                Text = "Orientation:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 45)
            });

            _cmbOrientation = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(80, 80, 85),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                Location = new Point(110, 43),
                Width = 80
            };
            _cmbOrientation.Items.AddRange(new object[] { "Vertical", "Horizontal" });
            _cmbOrientation.SelectedIndex = _isVertical ? 0 : 1;
            _cmbOrientation.SelectedIndexChanged += (s, e) =>
            {
                _isVertical = _cmbOrientation.SelectedIndex == 0;
                UpdateDiameterViewOptions();
                RefreshAll();
            };
            propsPanel.Controls.Add(_cmbOrientation);

            // Diameter View label and control
            propsPanel.Controls.Add(new Label
            {
                Text = "Diameter View:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(200, 45)
            });

            _cmbDiameterView = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(80, 80, 85),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                Location = new Point(280, 43),
                Width = 80
            };
            _cmbDiameterView.SelectedIndexChanged += (s, e) =>
            {
                if (_cmbDiameterView.SelectedIndex >= 0)
                {
                    _diameterView = (View)_cmbDiameterView.SelectedIndex;
                    RefreshAll();
                }
            };
            propsPanel.Controls.Add(_cmbDiameterView);
            UpdateDiameterViewOptions();

            // Show core checkbox
            var chkShow = new CheckBox
            {
                Text = "Show core",
                Checked = _showCore,
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 75)
            };
            chkShow.CheckedChanged += (s, e) => { _showCore = chkShow.Checked; RefreshAll(); };
            propsPanel.Controls.Add(chkShow);

            // Help text
            var helpLabel = new Label
            {
                Text = "Use mouse: Left-click + drag to move core center.\nLeft-click on edge + drag to resize diameter.",
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 7.5f),
                AutoSize = true,
                Location = new Point(10, 105)
            };
            propsPanel.Controls.Add(helpLabel);

            // Actions panel - right panel
            var buttonGroup = new GroupBox
            {
                Text = "Actions",
                ForeColor = Color.White,
                BackColor = Color.FromArgb(60, 60, 65),
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                Margin = new Padding(5)
            };
            bottomLayout.Controls.Add(buttonGroup, 1, 0);

            // Button layout in a table
            var buttonLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 2,
                BackColor = Color.Transparent
            };
            buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            buttonLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            buttonLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            buttonGroup.Controls.Add(buttonLayout);

            // Add buttons to the layout
            buttonLayout.Controls.Add(MakeButton("Auto-detect", AutoDetectAsync), 0, 0);
            buttonLayout.Controls.Add(MakeButton("Apply", ApplyCoreAsync), 1, 0);
            buttonLayout.Controls.Add(MakeButton("Close", () => Close()), 2, 0);
            buttonLayout.Controls.Add(MakeButton("Switch Views", SwitchViews), 0, 1);
            buttonLayout.Controls.Add(MakeButton("Reset View", ResetView), 1, 1);
            buttonLayout.Controls.Add(MakeButton("Help", ShowHelp), 2, 1);

            FormClosing += (s, e) =>
            {
                _renderCts.Cancel();
                DisposeCaches();
            };
        }

        private void UpdateDiameterViewOptions()
        {
            _cmbDiameterView.Items.Clear();

            if (_isVertical)
            {
                // For vertical orientation, diameter is visible in XY view
                _cmbDiameterView.Items.AddRange(new object[] { "XY" });
                _diameterView = View.XY;
            }
            else
            {
                // For horizontal orientation, diameter can be visible in different views
                // depending on axis alignment
                _cmbDiameterView.Items.AddRange(new object[] { "XY", "XZ", "YZ" });
            }

            _cmbDiameterView.SelectedIndex = (int)_diameterView;
        }

        private Button MakeButton(string txt, Action click)
        {
            var b = new Button
            {
                Text = txt,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(80, 80, 85),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f),
                UseVisualStyleBackColor = false,
                Margin = new Padding(10, 5, 10, 5)
            };
            b.Click += (s, e) => click();
            return b;
        }

        private void CreateViewPanel(string title, View v, out Panel host, out PictureBox pic,
                                     out TrackBar track, out NumericUpDown num, out Label lbl)
        {
            host = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48),
                Margin = new Padding(0),
                Padding = new Padding(3)
            };

            lbl = new Label
            {
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top,
                Height = 22,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Text = title
            };
            host.Controls.Add(lbl);

            pic = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black,
                Margin = new Padding(0)
            };
            host.Controls.Add(pic);

            // Control panel for the slider and numeric up/down
            var controlPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 60, // Increased height for better spacing
                BackColor = host.BackColor,
                Margin = new Padding(0),
                ColumnCount = 1,
                RowCount = 2,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            controlPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f)); // Row for trackbar
            controlPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25f)); // Row for numeric
            host.Controls.Add(controlPanel);

            // Add track to the top row
            track = new TrackBar
            {
                Dock = DockStyle.Fill,
                TickStyle = TickStyle.None,
                Minimum = 0,
                Maximum = GetSliceMax(v),
                Margin = new Padding(5, 3, 5, 0)
            };
            controlPanel.Controls.Add(track, 0, 0);

            // Add numeric to the bottom row
            num = new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(20, 3, 20, 3),
                Minimum = 0,
                Maximum = GetSliceMax(v),
                BackColor = Color.FromArgb(60, 60, 65),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Center
            };
            controlPanel.Controls.Add(num, 0, 1);

            // link track & numeric
            var t = track;
            var n = num;
            t.ValueChanged += (s, e) => { n.Value = t.Value; SetSlice(v, t.Value); };
            n.ValueChanged += (s, e) => { t.Value = (int)n.Value; SetSlice(v, (int)n.Value); };

            // mouse interaction
            pic.MouseDown += (s, e) => PicMouseDown(v, e);
            pic.MouseMove += (s, e) => PicMouseMove(v, e);
            pic.MouseUp += (s, e) => PicMouseUp();
            pic.MouseWheel += (s, e) => PicMouseWheel(v, e);
            pic.Paint += (s, e) => PicPaint(v, e);
        }
        #endregion

        #region ========== View helpers ==========
        private int GetSliceMax(View v)
        {
            switch (v)
            {
                case View.XY: return _d - 1;
                case View.XZ: return _h - 1;
                case View.YZ: return _w - 1;
                default: return 0;
            }
        }

        private void InitSlicePositions()
        {
            _xySlice = _d / 2; _xzSlice = _h / 2; _yzSlice = _w / 2;
            _xyTrack.Value = _xySlice; _xzTrack.Value = _xzSlice; _yzTrack.Value = _yzSlice;
            _xyNum.Value = _xySlice; _xzNum.Value = _xzSlice; _yzNum.Value = _yzSlice;
            UpdateLabels();
        }

        private void SetSlice(View v, int value)
        {
            switch (v)
            {
                case View.XY: _xySlice = value; break;
                case View.XZ: _xzSlice = value; break;
                case View.YZ: _yzSlice = value; break;
            }
            UpdateLabels();
            RenderSliceAsync(v);                 // fire & forget
        }

        private void UpdateLabels()
        {
            _xyLbl.Text = $"XY – Z {_xySlice + 1}/{_d}";
            _xzLbl.Text = $"XZ – Y {_xzSlice + 1}/{_h}";
            _yzLbl.Text = $"YZ – X {_yzSlice + 1}/{_w}";
        }
        #endregion

        #region ========== Rendering ==========
        private async void RenderAllAsync()
        {
            _renderCts.Cancel();
            _renderCts = new CancellationTokenSource();
            var ct = _renderCts.Token;

            try
            {
                await Task.WhenAll(RenderSliceAsync(View.XY, ct),
                                   RenderSliceAsync(View.XZ, ct),
                                   RenderSliceAsync(View.YZ, ct));
            }
            catch { /* cancelled */ }
        }

        private void RenderSliceAsync(View v) => RenderSliceAsync(v, _renderCts.Token);

        private async Task RenderSliceAsync(View v, CancellationToken ct)
        {
            try
            {
                var pic = GetPic(v);
                var sliceIdx = GetSlice(v);

                Bitmap bmp = GetCache(v, sliceIdx);
                if (bmp == null)
                {
                    bmp = await Task.Run(() => RenderSlice(v, sliceIdx), ct);
                    AddCache(v, sliceIdx, bmp);
                }
                if (ct.IsCancellationRequested) return;

                await this.SafeInvokeAsync(() =>
                {
                    pic.Image?.Dispose();
                    pic.Image = bmp;
                    pic.Invalidate();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Log($"Render {v} failed: {ex.Message}"); }
        }

        private Bitmap RenderSlice(View v, int idx)
        {
            int w, h;
            switch (v)
            {
                case View.XY: w = _w; h = _h; break;
                case View.XZ: w = _w; h = _d; break;
                case View.YZ: w = _d; h = _h; break;
                default: return null;
            }

            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bmp.PixelFormat);

            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                Parallel.For(0, h, y =>
                {
                    byte* row = basePtr + y * data.Stride;
                    for (int x = 0; x < w; ++x)
                    {
                        byte g;
                        switch (v)
                        {
                            case View.XY: g = _main.volumeData[x, y, idx]; break;
                            case View.XZ: g = _main.volumeData[x, idx, y]; break;
                            case View.YZ: g = _main.volumeData[idx, y, x]; break;
                            default: g = 0; break;
                        }
                        int offset = x * 3;
                        row[offset] = g;
                        row[offset + 1] = g;
                        row[offset + 2] = g;
                    }
                });
            }
            bmp.UnlockBits(data);
            return bmp;
        }
        #endregion

        #region ========== Mouse interaction ==========
        private void PicMouseDown(View v, MouseEventArgs e)
        {
            if (!_showCore || e.Button != MouseButtons.Left) return;

            _activeView = v;
            _lastMouseScreen = e.Location;

            // Only allow interaction with the diameter in the selected diameter view
            // or with length in the other views
            bool canInteractWithDiameter = (v == _diameterView);

            var c = ViewToScreen(v, _center);
            int r = canInteractWithDiameter ? ViewRadius(v) : 0;

            int dist = (int)Math.Sqrt((e.X - c.X) * (e.X - c.X) + (e.Y - c.Y) * (e.Y - c.Y));

            // Drag center if mouse is close to center point
            if (dist < 15)
                _dragCenter = true;
            // Resize diameter only in diameter view and when mouse is close to circle edge
            else if (canInteractWithDiameter && Math.Abs(dist - r) < 10)
                _resize = true;
        }

        private void PicMouseMove(View v, MouseEventArgs e)
        {
            if (!_showCore || v != _activeView) return;

            var pic = GetPic(v);
            double sx = _w / (double)pic.ClientSize.Width;
            double sy = _h / (double)pic.ClientSize.Height;

            int dx = e.X - _lastMouseScreen.X;
            int dy = e.Y - _lastMouseScreen.Y;

            if (_dragCenter)
            {
                switch (v)
                {
                    case View.XY: _center.X += (float)(dx * sx); _center.Y += (float)(dy * sy); break;
                    case View.XZ: _center.X += (float)(dx * sx); _center.Z += (float)(dy * sy); break;
                    case View.YZ: _center.Z += (float)(dx * sx); _center.Y += (float)(dy * sy); break;
                }
                RefreshAll();
            }
            else if (_resize && v == _diameterView)
            {
                var c = ViewToScreen(v, _center);
                double d = Math.Sqrt((e.X - c.X) * (e.X - c.X) + (e.Y - c.Y) * (e.Y - c.Y));
                double scale = _w / (double)pic.ClientSize.Width;
                _diameter = (float)(d * scale * 2); // * 2 because d is radius and we store diameter
                _numDiameter.Value = (decimal)_diameter;
                RefreshAll();
            }

            _lastMouseScreen = e.Location;
        }

        private void PicMouseUp() { _dragCenter = _resize = false; }

        private void PicMouseWheel(View v, MouseEventArgs e)
        {
            if (ModifierKeys.HasFlag(Keys.Control))
            {
                _length = Math.Max(1, _length + (e.Delta > 0 ? 10 : -10));
                _numLength.Value = (decimal)_length;
                RefreshAll();
            }
            else if (ModifierKeys.HasFlag(Keys.Shift))
            {
                _diameter = Math.Max(1, _diameter + (e.Delta > 0 ? 5 : -5));
                _numDiameter.Value = (decimal)_diameter;
                RefreshAll();
            }
            else
            {
                int delta = e.Delta > 0 ? -1 : 1;
                var t = GetTrack(v);
                t.Value = Math.Max(t.Minimum, Math.Min(t.Maximum, t.Value + delta));
            }
        }

        private void PicPaint(View v, PaintEventArgs e)
        {
            if (!_showCore) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var c = ViewToScreen(v, _center);

            // For the diameter view, draw a circle representing the cross-section
            if (v == _diameterView)
            {
                int r = ViewRadius(v);
                using (var pen = new Pen(Color.Red, 2))
                    g.DrawEllipse(pen, c.X - r, c.Y - r, r * 2, r * 2);
            }
            // For other views, draw lines representing the core's length
            else
            {
                float halfLength = _length / 2;

                // Calculate endpoints based on orientation
                Point p1, p2;

                if (_isVertical)
                {
                    // Vertical core - lines go up/down from center
                    if (v == View.XZ)
                    {
                        p1 = new Point(c.X, (int)(c.Y - ViewLength(v) / 2));
                        p2 = new Point(c.X, (int)(c.Y + ViewLength(v) / 2));
                    }
                    else // YZ view
                    {
                        p1 = new Point(c.X, (int)(c.Y - ViewLength(v) / 2));
                        p2 = new Point(c.X, (int)(c.Y + ViewLength(v) / 2));
                    }
                }
                else
                {
                    // Horizontal core - the lines depend on which axis it's aligned with
                    // This is determined by which view is selected for diameter
                    if (_diameterView == View.XY)
                    {
                        // Core aligned with Z axis
                        if (v == View.XZ)
                        {
                            p1 = new Point((int)(c.X - ViewLength(v) / 2), c.Y);
                            p2 = new Point((int)(c.X + ViewLength(v) / 2), c.Y);
                        }
                        else // YZ view
                        {
                            p1 = new Point((int)(c.X - ViewLength(v) / 2), c.Y);
                            p2 = new Point((int)(c.X + ViewLength(v) / 2), c.Y);
                        }
                    }
                    else if (_diameterView == View.XZ)
                    {
                        // Core aligned with Y axis
                        if (v == View.XY)
                        {
                            p1 = new Point(c.X, (int)(c.Y - ViewLength(v) / 2));
                            p2 = new Point(c.X, (int)(c.Y + ViewLength(v) / 2));
                        }
                        else // YZ view
                        {
                            p1 = new Point(c.X, (int)(c.Y - ViewLength(v) / 2));
                            p2 = new Point(c.X, (int)(c.Y + ViewLength(v) / 2));
                        }
                    }
                    else // _diameterView == View.YZ
                    {
                        // Core aligned with X axis
                        if (v == View.XY)
                        {
                            p1 = new Point((int)(c.X - ViewLength(v) / 2), c.Y);
                            p2 = new Point((int)(c.X + ViewLength(v) / 2), c.Y);
                        }
                        else // XZ view
                        {
                            p1 = new Point((int)(c.X - ViewLength(v) / 2), c.Y);
                            p2 = new Point((int)(c.X + ViewLength(v) / 2), c.Y);
                        }
                    }
                }

                // Draw the line representing core length
                using (var pen = new Pen(Color.Red, 2))
                    g.DrawLine(pen, p1, p2);
            }

            // Draw center point on all views
            g.FillEllipse(Brushes.Yellow, c.X - 4, c.Y - 4, 8, 8);
        }
        #endregion

        #region ========== Helpers ==========
        private PictureBox GetPic(View v) => v == View.XY ? _xyPic : v == View.XZ ? _xzPic : _yzPic;
        private TrackBar GetTrack(View v) => v == View.XY ? _xyTrack : v == View.XZ ? _xzTrack : _yzTrack;
        private int GetSlice(View v) => v == View.XY ? _xySlice : v == View.XZ ? _xzSlice : _yzSlice;

        private Point ViewToScreen(View v, Point3D p)
        {
            switch (v)
            {
                case View.XY: return new Point((int)(p.X * _xyPic.Width / _w), (int)(p.Y * _xyPic.Height / _h));
                case View.XZ: return new Point((int)(p.X * _xzPic.Width / _w), (int)(p.Z * _xzPic.Height / _d));
                default: return new Point((int)(p.Z * _yzPic.Width / _d), (int)(p.Y * _yzPic.Height / _h));
            }
        }

        // Convert diameter to view-specific radius
        private int ViewRadius(View v)
        {
            var pic = GetPic(v);
            return (int)((_diameter / 2) * pic.Width / _w);
        }

        // Convert length to view-specific length
        private float ViewLength(View v)
        {
            var pic = GetPic(v);
            float scaleRatio;

            switch (v)
            {
                case View.XY: scaleRatio = pic.Width / (float)_w; break;
                case View.XZ: scaleRatio = pic.Width / (float)_w; break;
                default: scaleRatio = pic.Height / (float)_h; break;
            }

            return _length * scaleRatio;
        }

        private Bitmap GetCache(View v, int k) => v == View.XY ? _xyCache.Get(k) : v == View.XZ ? _xzCache.Get(k) : _yzCache.Get(k);
        private void AddCache(View v, int k, Bitmap b)
        {
            if (v == View.XY) _xyCache.Add(k, b);
            else if (v == View.XZ) _xzCache.Add(k, b);
            else _yzCache.Add(k, b);
        }

        private void RefreshAll()
        {
            _xyPic.Invalidate(); _xzPic.Invalidate(); _yzPic.Invalidate();
        }

        private void DisposeCaches()
        {
            DisposeCache(_xyCache);
            DisposeCache(_xzCache);
            DisposeCache(_yzCache);
        }
        private static void DisposeCache(LRUCache<int, Bitmap> cache)
        {
            foreach (int k in cache.GetKeys())
            {
                var bmp = cache.Get(k);
                bmp?.Dispose();
            }
        }
        #endregion

        #region ========== Buttons ==========
        private async void AutoDetectAsync()
        {
            Enabled = false;
            try
            {
                // 1. Ask the user which view to use for diameter detection
                View selectedView;
                using (var viewDialog = new DialogForm("Select View for Diameter Detection",
                                                     "Which view should be used to detect the core diameter?",
                                                     new string[] { "XY View", "XZ View", "YZ View" }))
                {
                    if (viewDialog.ShowDialog(this) != DialogResult.OK)
                    {
                        // User cancelled
                        return;
                    }

                    // Convert selection to View enum
                    selectedView = (View)viewDialog.SelectedIndex;
                }

                // Set as the diameter view
                _diameterView = selectedView;
                _cmbDiameterView.SelectedIndex = (int)_diameterView;

                using (var pf = new ProgressForm("Detecting core…"))
                {
                    var cts = new CancellationTokenSource();
                    pf.FormClosed += (s, e) => cts.Cancel();

                    pf.Show(this);

                    // Use the direct shrinking circle method
                    await Task.Run(() => ShrinkingCircleDetection(selectedView, pf, cts.Token), cts.Token);
                }

                // Update UI with the detected values
                _numDiameter.Value = (decimal)_diameter;
                _numLength.Value = (decimal)_length;
                RefreshAll();

                MessageBox.Show("Core detected successfully!", "Auto-detection",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException) { /* user cancelled */ }
            catch (Exception ex)
            {
                // Log the specific error for debugging
                Logger.Log($"Auto-detect error: {ex.Message}");
                MessageBox.Show($"Detection error: {ex.Message}", "Detect error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { Enabled = true; }
        }

        // Dialog form for view selection
        private class DialogForm : Form
        {
            public int SelectedIndex { get; private set; }
            private ComboBox _comboBox;

            public DialogForm(string title, string message, string[] options)
            {
                Text = title;
                Size = new Size(350, 180);
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;

                var label = new Label
                {
                    Text = message,
                    Location = new Point(20, 20),
                    Size = new Size(310, 40),
                    TextAlign = ContentAlignment.MiddleLeft
                };

                _comboBox = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new Point(20, 70),
                    Size = new Size(310, 25),
                    BackColor = Color.FromArgb(80, 80, 85),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat
                };

                _comboBox.Items.AddRange(options);
                _comboBox.SelectedIndex = 0;

                var btnOk = new Button
                {
                    Text = "OK",
                    Location = new Point(130, 110),
                    Size = new Size(90, 30),
                    BackColor = Color.FromArgb(80, 80, 85),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    DialogResult = DialogResult.OK
                };

                btnOk.Click += (s, e) =>
                {
                    SelectedIndex = _comboBox.SelectedIndex;
                    DialogResult = DialogResult.OK;
                    Close();
                };

                Controls.Add(label);
                Controls.Add(_comboBox);
                Controls.Add(btnOk);

                AcceptButton = btnOk;
            }
        }

        // Shrinking circle detection method as specified
        private void ShrinkingCircleDetection(View view, ProgressForm pf, CancellationToken token)
        {
            try
            {
                // Get dimensions for the selected view
                int width, height, slice;
                Point3D centerPoint = new Point3D(_w / 2f, _h / 2f, _d / 2f);

                switch (view)
                {
                    case View.XY:
                        width = _w;
                        height = _h;
                        slice = _xySlice;
                        break;
                    case View.XZ:
                        width = _w;
                        height = _d;
                        slice = _xzSlice;
                        break;
                    case View.YZ:
                        width = _d;
                        height = _h;
                        slice = _yzSlice;
                        break;
                    default:
                        throw new ArgumentException("Invalid view");
                }

                // 2. Create a circle from the middle with radius = larger slice border / 2
                int initialRadius = Math.Min(width, height) / 2;
                int x0 = width / 2;  // Center X
                int y0 = height / 2; // Center Y

                pf.SafeUpdate(0, 100, "Sampling reference color...");

                // 3. Sample color in upper left corner (outside the core)
                int cornerX = width / 10;  // 10% from left
                int cornerY = height / 10; // 10% from top

                int referenceColor = GetIntensityAtPosition(cornerX, cornerY, slice, view);
                int tolerance = 20; // Tolerance for small variations (noise)

                pf.SafeUpdate(10, 100, "Detecting core boundary...");

                // 4 & 5. Make the circle smaller and check circumference
                int finalRadius = initialRadius;
                int centerAdjustmentStep = 5; // Step size for adjusting center
                int radiusStep = 5;           // Step size for reducing radius

                // Center adjustment variables
                int centerX = x0;
                int centerY = y0;

                for (int radius = initialRadius; radius >= initialRadius / 5; radius -= radiusStep)
                {
                    token.ThrowIfCancellationRequested();
                    pf.SafeUpdate(10 + (int)(90.0 * (initialRadius - radius) / initialRadius), 100,
                              $"Testing radius {radius}...");

                    bool allPointsDifferent = true;
                    int sumX = 0, sumY = 0, pointCount = 0;

                    // Check points on the circumference (every 15 degrees)
                    for (int angle = 0; angle < 360; angle += 15)
                    {
                        double rad = angle * Math.PI / 180;
                        int x = (int)(centerX + radius * Math.Cos(rad));
                        int y = (int)(centerY + radius * Math.Sin(rad));

                        // Ensure point is within bounds
                        if (x >= 0 && x < width && y >= 0 && y < height)
                        {
                            int pointColor = GetIntensityAtPosition(x, y, slice, view);

                            // Check if this point is similar to reference (outside) color
                            if (Math.Abs(pointColor - referenceColor) <= tolerance)
                            {
                                // This point is still "outside" the core
                                allPointsDifferent = false;

                                // Sum position for center adjustment
                                sumX += x;
                                sumY += y;
                                pointCount++;
                            }
                        }
                    }

                    // 5. Adjust center if needed
                    if (!allPointsDifferent && pointCount > 0)
                    {
                        // Move center away from points that are still like the reference
                        int newCenterX = centerX - (sumX / pointCount - centerX) / centerAdjustmentStep;
                        int newCenterY = centerY - (sumY / pointCount - centerY) / centerAdjustmentStep;

                        // Keep center within bounds
                        centerX = Math.Max(radius, Math.Min(width - radius, newCenterX));
                        centerY = Math.Max(radius, Math.Min(height - radius, newCenterY));
                    }

                    // 6. Process is complete when all points are different from the reference
                    if (allPointsDifferent)
                    {
                        finalRadius = radius;

                        // Update center and radius
                        switch (view)
                        {
                            case View.XY:
                                centerPoint.X = centerX;
                                centerPoint.Y = centerY;
                                break;
                            case View.XZ:
                                centerPoint.X = centerX;
                                centerPoint.Z = centerY;
                                break;
                            case View.YZ:
                                centerPoint.Z = centerX;
                                centerPoint.Y = centerY;
                                break;
                        }

                        break;
                    }
                }

                // Set the detected values
                _center = centerPoint;
                _diameter = finalRadius * 2;

                // Estimate length (typically 2-3 times the diameter for a core)
                _length = Math.Min(_d * 0.8f, _diameter * 2.5f);

                pf.SafeUpdate(100, 100, "Core detection complete!");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in ShrinkingCircleDetection: {ex.Message}");
                throw;
            }
        }

        // Helper method to get intensity at a given position based on view
        private int GetIntensityAtPosition(int x, int y, int slice, View view)
        {
            try
            {
                switch (view)
                {
                    case View.XY:
                        return _main.volumeData[x, y, slice];
                    case View.XZ:
                        return _main.volumeData[x, slice, y];
                    case View.YZ:
                        return _main.volumeData[slice, y, x];
                    default:
                        throw new ArgumentException("Invalid view");
                }
            }
            catch (IndexOutOfRangeException)
            {
                // Return a default value if out of bounds
                return 0;
            }
        }

        // Estimate radius in XY plane by analyzing image data directly
        private float EstimateRadiusInXYPlane(int z, CancellationToken token)
        {
            try
            {
                // Find the approximate center of the image
                int centerX = _w / 2;
                int centerY = _h / 2;

                // First attempt a direct core size estimate based on visible core boundary
                float estimatedRadius = Math.Min(_w, _h) * 0.35f; // Start with a reasonable size (35% of width/height)

                // Sample intensity at various distances from center
                Dictionary<int, int> intensityByRadius = new Dictionary<int, int>();
                int maxRadius = Math.Min(_w, _h) / 2 - 10; // Don't go all the way to the edge

                // Start from 5% to 50% of the image width, sampling every 5% interval
                int step = Math.Max(1, maxRadius / 20);

                // Build intensity profile from center to edge
                for (int r = 5; r < maxRadius; r += step)
                {
                    int totalIntensity = 0;
                    int sampleCount = 0;

                    // Sample in a ring at this radius
                    for (int angle = 0; angle < 360; angle += 15)
                    {
                        double rad = angle * Math.PI / 180;
                        int x = (int)(centerX + Math.Cos(rad) * r);
                        int y = (int)(centerY + Math.Sin(rad) * r);

                        if (x >= 0 && x < _w && y >= 0 && y < _h && z >= 0 && z < _d)
                        {
                            totalIntensity += _main.volumeData[x, y, z];
                            sampleCount++;
                        }
                    }

                    if (sampleCount > 0)
                    {
                        intensityByRadius[r] = totalIntensity / sampleCount;
                    }
                }

                // Analyze the intensity profile to find significant transitions (core boundary)
                List<int> potentialBoundaries = new List<int>();

                // Calculate running average over a window to smoothen the profile
                int windowSize = 3;
                List<KeyValuePair<int, double>> smoothedProfile = new List<KeyValuePair<int, double>>();

                foreach (var radius in intensityByRadius.Keys.OrderBy(r => r))
                {
                    // Calculate average of neighboring radius values
                    double sum = 0;
                    int count = 0;

                    for (int i = -windowSize / 2; i <= windowSize / 2; i++)
                    {
                        int r = radius + i * step;
                        if (intensityByRadius.ContainsKey(r))
                        {
                            sum += intensityByRadius[r];
                            count++;
                        }
                    }

                    if (count > 0)
                    {
                        smoothedProfile.Add(new KeyValuePair<int, double>(radius, sum / count));
                    }
                }

                // Look for significant intensity gradient changes in the smoothed profile
                double maxGradient = 0;
                int coreBoundaryRadius = 0;

                // Must have at least 2 points to calculate gradient
                if (smoothedProfile.Count > 2)
                {
                    for (int i = 1; i < smoothedProfile.Count; i++)
                    {
                        double gradient = Math.Abs(smoothedProfile[i].Value - smoothedProfile[i - 1].Value) /
                                         (smoothedProfile[i].Key - smoothedProfile[i - 1].Key);

                        // If this is a significant gradient change and we're away from the center
                        if (gradient > maxGradient && smoothedProfile[i].Key > maxRadius * 0.15)
                        {
                            maxGradient = gradient;
                            coreBoundaryRadius = smoothedProfile[i].Key;
                        }
                    }
                }

                // If we found a boundary
                if (coreBoundaryRadius > 0)
                {
                    estimatedRadius = coreBoundaryRadius;
                }
                else
                {
                    // Fallback to a reasonable default based on image size
                    estimatedRadius = Math.Min(_w, _h) * 0.35f;
                }

                // Based on the core being clearly visible in the image, ensure we have a large enough circle
                // Clamp to a minimum reasonable size
                float minRadius = Math.Min(_w, _h) * 0.3f;
                if (estimatedRadius < minRadius)
                {
                    estimatedRadius = minRadius;
                }

                // Clamp the radius to a reasonable maximum
                float maxRadiusLimit = Math.Min(_w, _h) * 0.45f; // Maximum 45% of image size
                if (estimatedRadius > maxRadiusLimit)
                {
                    estimatedRadius = maxRadiusLimit;
                }

                return estimatedRadius;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in EstimateRadiusInXYPlane: {ex.Message}");
                // Return a reasonable default if estimation fails - larger to encompass the core
                return Math.Min(_w, _h) * 0.35f;
            }
        }

        // Estimate core parameters from the current view
        private void ManualEstimateFromCurrentView()
        {
            // Get current view dimensions
            int fullWidth = GetViewSize(_diameterView).Width;
            int fullHeight = GetViewSize(_diameterView).Height;

            // Determine reasonable defaults based on view size
            float estimatedDiameter = Math.Min(fullWidth, fullHeight) * 0.6f;

            // Update the core parameters
            _diameter = estimatedDiameter;
            _numDiameter.Value = Math.Min(_numDiameter.Maximum, Math.Max(_numDiameter.Minimum, (decimal)_diameter));

            // Keep the center point at the center of the current view
            switch (_diameterView)
            {
                case View.XY:
                    _center.X = _w / 2f;
                    _center.Y = _h / 2f;
                    break;
                case View.XZ:
                    _center.X = _w / 2f;
                    _center.Z = _d / 2f;
                    break;
                case View.YZ:
                    _center.Y = _h / 2f;
                    _center.Z = _d / 2f;
                    break;
            }

            // Refresh the visualization
            RefreshAll();
        }

        private Size GetViewSize(View v)
        {
            switch (v)
            {
                case View.XY: return new Size(_w, _h);
                case View.XZ: return new Size(_w, _d);
                case View.YZ: return new Size(_d, _h);
                default: return Size.Empty;
            }
        }

        private async void ApplyCoreAsync()
        {
            if (MessageBox.Show("Remove all voxels outside the core?",
                                "Confirm",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            Enabled = false;
            try
            {
                using (var pf = new ProgressForm("Extracting core…"))
                {
                    var cts = new CancellationTokenSource();
                    pf.FormClosed += (s, e) => cts.Cancel();

                    pf.Show(this);
                    await Task.Run(() => ExtractCore(pf, cts.Token), cts.Token);
                }
                await _main.RenderOrthoViewsAsync();
                MessageBox.Show("Core extracted.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException) { /* user cancelled */ }
            finally { Enabled = true; }
        }

        private void ExtractCore(ProgressForm pf, CancellationToken token)
        {
            byte exterior = _main.Materials.Find(m => m.IsExterior).ID;
            float radius = _diameter / 2;

            // Calculate bounds based on orientation
            int minX, maxX, minY, maxY, minZ, maxZ;

            if (_isVertical)
            {
                minX = (int)(_center.X - radius - 1);
                maxX = (int)(_center.X + radius + 1);
                minY = (int)(_center.Y - radius - 1);
                maxY = (int)(_center.Y + radius + 1);
                minZ = (int)(_center.Z - _length / 2 - 1);
                maxZ = (int)(_center.Z + _length / 2 + 1);
            }
            else
            {
                // Bounds depend on which axis the core is aligned with
                if (_diameterView == View.XY)
                {
                    // Aligned with Z axis
                    minX = (int)(_center.X - radius - 1);
                    maxX = (int)(_center.X + radius + 1);
                    minY = (int)(_center.Y - radius - 1);
                    maxY = (int)(_center.Y + radius + 1);
                    minZ = (int)(_center.Z - _length / 2 - 1);
                    maxZ = (int)(_center.Z + _length / 2 + 1);
                }
                else if (_diameterView == View.XZ)
                {
                    // Aligned with Y axis
                    minX = (int)(_center.X - radius - 1);
                    maxX = (int)(_center.X + radius + 1);
                    minY = (int)(_center.Y - _length / 2 - 1);
                    maxY = (int)(_center.Y + _length / 2 + 1);
                    minZ = (int)(_center.Z - radius - 1);
                    maxZ = (int)(_center.Z + radius + 1);
                }
                else // _diameterView == View.YZ
                {
                    // Aligned with X axis
                    minX = (int)(_center.X - _length / 2 - 1);
                    maxX = (int)(_center.X + _length / 2 + 1);
                    minY = (int)(_center.Y - radius - 1);
                    maxY = (int)(_center.Y + radius + 1);
                    minZ = (int)(_center.Z - radius - 1);
                    maxZ = (int)(_center.Z + radius + 1);
                }
            }

            // Clamp bounds to valid ranges
            minX = Math.Max(0, minX);
            maxX = Math.Min(_w - 1, maxX);
            minY = Math.Max(0, minY);
            maxY = Math.Min(_h - 1, maxY);
            minZ = Math.Max(0, minZ);
            maxZ = Math.Min(_d - 1, maxZ);

            Parallel.For(0, _d, new ParallelOptions { CancellationToken = token }, z =>
            {
                if (z % 5 == 0) pf.SafeUpdate(z, _d, $"Slice {z + 1}/{_d}");
                if (z < minZ || z > maxZ) { ZeroSlice(z, exterior); return; }

                for (int y = 0; y < _h; y++)
                {
                    if (y < minY || y > maxY) { ZeroRow(z, y, exterior); continue; }
                    for (int x = 0; x < _w; x++)
                    {
                        if (x < minX || x > maxX || !InsideCylinder(x, y, z))
                            _main.volumeLabels[x, y, z] = exterior;
                    }
                }
            });
            _main.SaveLabelsChk();
        }

        private bool InsideCylinder(int x, int y, int z)
        {
            float radius = _diameter / 2;

            if (_isVertical)
            {
                double dx = x - _center.X, dy = y - _center.Y;
                return dx * dx + dy * dy <= radius * radius &&
                       Math.Abs(z - _center.Z) <= _length / 2;
            }
            else
            {
                // Check based on which axis the core is aligned with
                if (_diameterView == View.XY)
                {
                    // Aligned with Z axis
                    double dx = x - _center.X, dy = y - _center.Y;
                    return dx * dx + dy * dy <= radius * radius &&
                           Math.Abs(z - _center.Z) <= _length / 2;
                }
                else if (_diameterView == View.XZ)
                {
                    // Aligned with Y axis
                    double dx = x - _center.X, dz = z - _center.Z;
                    return dx * dx + dz * dz <= radius * radius &&
                           Math.Abs(y - _center.Y) <= _length / 2;
                }
                else // _diameterView == View.YZ
                {
                    // Aligned with X axis
                    double dy = y - _center.Y, dz = z - _center.Z;
                    return dy * dy + dz * dz <= radius * radius &&
                           Math.Abs(x - _center.X) <= _length / 2;
                }
            }
        }

        private void ZeroSlice(int z, byte exterior)
        { for (int y = 0; y < _h; y++) for (int x = 0; x < _w; x++) _main.volumeLabels[x, y, z] = exterior; }

        private void ZeroRow(int z, int y, byte exterior)
        { for (int x = 0; x < _w; x++) _main.volumeLabels[x, y, z] = exterior; }

        private void SwitchViews()
        {
            _currentLayoutIndex = (_currentLayoutIndex + 1) % 3;
            _layout.SuspendLayout();
            switch (_currentLayoutIndex)
            {
                case 0:
                    _layout.SetCellPosition(_xyPic.Parent, new TableLayoutPanelCellPosition(0, 0));
                    _layout.SetCellPosition(_xzPic.Parent, new TableLayoutPanelCellPosition(1, 0));
                    _layout.SetCellPosition(_yzPic.Parent, new TableLayoutPanelCellPosition(2, 0));
                    break;
                case 1:
                    _layout.SetCellPosition(_xzPic.Parent, new TableLayoutPanelCellPosition(0, 0));
                    _layout.SetCellPosition(_yzPic.Parent, new TableLayoutPanelCellPosition(1, 0));
                    _layout.SetCellPosition(_xyPic.Parent, new TableLayoutPanelCellPosition(2, 0));
                    break;
                case 2:
                    _layout.SetCellPosition(_yzPic.Parent, new TableLayoutPanelCellPosition(0, 0));
                    _layout.SetCellPosition(_xyPic.Parent, new TableLayoutPanelCellPosition(1, 0));
                    _layout.SetCellPosition(_xzPic.Parent, new TableLayoutPanelCellPosition(2, 0));
                    break;
            }
            _layout.ResumeLayout();
        }

        private void ResetView()
        {
            // Reset center, diameter, and length to defaults
            _center = new Point3D(_w / 2f, _h / 2f, _d / 2f);
            _diameter = Math.Min(_w, _h) / 2f;
            _length = _d * 0.8f;

            // Reset orientation to vertical
            _isVertical = true;
            _cmbOrientation.SelectedIndex = 0;

            // Reset diameter view to XY
            _diameterView = View.XY;
            UpdateDiameterViewOptions();

            // Update UI controls
            _numDiameter.Value = (decimal)_diameter;
            _numLength.Value = (decimal)_length;

            // Reset slice positions
            InitSlicePositions();

            // Refresh all views
            RefreshAll();
        }

        private void ShowHelp()
        {
            MessageBox.Show(
                "Core Extractor Help:\n\n" +
                "• Select which view shows the diameter using the 'Diameter View' dropdown\n" +
                "• Choose core orientation (vertical/horizontal) using the 'Orientation' dropdown\n" +
                "• Drag the yellow center point to reposition the core\n" +
                "• Drag the red circle edge in the diameter view to resize the core diameter\n" +
                "• Use mouse wheel + Ctrl to adjust core length\n" +
                "• Use mouse wheel + Shift to adjust core diameter\n" +
                "• After positioning the core, click 'Apply' to extract it",
                "Core Extractor Help",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        #endregion

        #region ========== Nested / helper structs ==========
        private class Point3D { public float X, Y, Z; public Point3D(float x, float y, float z) { X = x; Y = y; Z = z; } }
        private class Circle { public Point3D Center; public float Radius; }
        #endregion
    }

    // -------------------------------------------------------------------------
    // Additional extension so existing calls pf.SafeUpdate(...) still compile.
    // -------------------------------------------------------------------------
    internal static class ProgressFormExtensions
    {
        public static void SafeUpdate(this ProgressForm pf, int cur, int max, string msg)
        {
            pf.SafeInvokeAsync(() => pf.UpdateProgress(cur, max, msg));
        }
    }
}