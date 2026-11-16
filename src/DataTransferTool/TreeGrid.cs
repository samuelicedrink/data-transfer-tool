using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DataTransferTool
{
    /// <summary>A column of the tree-grid, drawn right of the names.</summary>
    internal sealed class GridColumn
    {
        public string Header;
        public int Width;
        public HorizontalAlignment Align = HorizontalAlignment.Left;
        /// <summary>When above 0, the column takes this share of the grid's width (at least Width).</summary>
        public double Share;

        internal int WidthIn(int gridWidth) => Share > 0 ? Math.Max(Width, (int)(gridWidth * Share)) : Width;
    }

    /// <summary>
    /// WinForms has no tree with columns. This is the native TreeView (so keyboard use, tick boxes,
    /// lazy loading and UI Automation stay as in v1) with the names drawn by the tool and the
    /// columns drawn on the right of each row. The columns are placed from the right edge, leaving
    /// room for the vertical scroll bar, so <see cref="TreeGridHeader"/> lines up with them whether
    /// or not the scroll bar is shown.
    /// </summary>
    internal sealed class TreeGrid : TreeView
    {
        public readonly List<GridColumn> Columns = new List<GridColumn>();
        /// <summary>Text of a cell; null or empty draws nothing.</summary>
        public Func<TreeNode, int, string> CellText;
        /// <summary>Colour of a node's name and cells; Empty = normal text colour.</summary>
        public Func<TreeNode, Color> NodeColor;
        /// <summary>Colour of one cell; Empty = the row's colour.</summary>
        public Func<TreeNode, int, Color> CellColor;

        public TreeGrid()
        {
            DrawMode = TreeViewDrawMode.OwnerDrawText;
            BorderStyle = BorderStyle.None;
            DoubleBuffered = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x8000; // TVS_NOHSCROLL: long names are cut with "..." instead
                return cp;
            }
        }

        /// <summary>X of the first column, in client coordinates.</summary>
        public int ColumnsLeft
        {
            get
            {
                int x = Width - SystemInformation.VerticalScrollBarWidth;
                foreach (var c in Columns) x -= c.WidthIn(Width);
                return x;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        /// <summary>Repaints one row, e.g. when its numbers arrive.</summary>
        public void InvalidateRow(TreeNode node)
        {
            if (node == null || node.TreeView != this || !node.IsVisible) return;
            var b = node.Bounds;
            Invalidate(new Rectangle(0, b.Y, ClientSize.Width, b.Height));
        }

        protected override void OnDrawNode(DrawTreeNodeEventArgs e)
        {
            var b = e.Bounds;
            if (b.IsEmpty || b.Height == 0) return;
            var g = e.Graphics;
            int left = ColumnsLeft;
            bool selected = (e.State & TreeNodeStates.Selected) != 0;
            bool focused = selected && Focused;
            Color custom = NodeColor?.Invoke(e.Node) ?? Color.Empty;

            // name, cut at the first column
            // measured here, not taken from the control's text box, which can be narrower (e.g. names with "&")
            const TextFormatFlags measure = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            int textWidth = TextRenderer.MeasureText(g, e.Node.Text, Font, new Size(int.MaxValue, b.Height), measure).Width + 4;
            int nameWidth = Math.Max(0, Math.Min(textWidth, left - 6 - b.X));
            var nameRect = new Rectangle(b.X, b.Y, nameWidth, b.Height);
            using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, new Rectangle(b.X, b.Y, Math.Max(0, left - b.X), b.Height));
            Color fore = custom.IsEmpty ? ForeColor : custom;
            if (selected && nameWidth > 0)
            {
                g.FillRectangle(focused ? SystemBrushes.Highlight : SystemBrushes.Control, nameRect);
                if (focused) fore = SystemColors.HighlightText;
            }
            TextRenderer.DrawText(g, e.Node.Text, Font, nameRect, fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if (focused && nameWidth > 0) ControlPaint.DrawFocusRectangle(g, nameRect, fore, SystemColors.Highlight);

            // cells
            int x = left;
            Color cellFore = custom.IsEmpty ? ForeColor : custom;
            using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, new Rectangle(left, b.Y, Width - left, b.Height));
            for (int i = 0; i < Columns.Count; i++)
            {
                var c = Columns[i];
                int w = c.WidthIn(Width);
                string text = CellText?.Invoke(e.Node, i);
                if (!string.IsNullOrEmpty(text))
                {
                    Color own = CellColor?.Invoke(e.Node, i) ?? Color.Empty;
                    var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                                (c.Align == HorizontalAlignment.Right ? TextFormatFlags.Right : c.Align == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left);
                    TextRenderer.DrawText(g, text, Font, new Rectangle(x + 4, b.Y, w - 8, b.Height), own.IsEmpty ? cellFore : own, flags);
                }
                x += w;
            }
        }
    }

    /// <summary>Column headings above a <see cref="TreeGrid"/>.</summary>
    internal sealed class TreeGridHeader : Control
    {
        readonly TreeGrid grid;

        public TreeGridHeader(TreeGrid grid, string nameHeader)
        {
            this.grid = grid;
            Text = nameHeader;
            Dock = DockStyle.Top;
            DoubleBuffered = true;
            Font = Ui.BoldFont;
            Height = TextRenderer.MeasureText("Ag", Font).Height + 8;
            grid.Resize += (s, e) => Invalidate();
            SetStyle(ControlStyles.Selectable, false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(SystemColors.Control);
            int left = grid.ColumnsLeft;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(6, 0, Math.Max(0, left - 10), Height), SystemColors.ControlText, flags);
            int x = left;
            foreach (var c in grid.Columns)
            {
                int w = c.WidthIn(grid.Width);
                g.DrawLine(SystemPens.ControlDark, x, 3, x, Height - 4);
                TextRenderer.DrawText(g, c.Header, Font, new Rectangle(x + 4, 0, w - 8, Height), SystemColors.ControlText,
                    flags | (c.Align == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left));
                x += w;
            }
            g.DrawLine(SystemPens.ControlDark, 0, Height - 1, Width, Height - 1);
        }
    }
}
