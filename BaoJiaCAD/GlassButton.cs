using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace BaoJiaCAD
{
    /// <summary>
    /// 🔧 v23 玻璃按钮 自绘控件 — 参考 GitHub 上开源的 WinForms GlassButton 实现思路.
    ///   - Rounded rect (圆角矩形) + vertical gradient (垂直渐变) + top highlight (顶部 高光) + 表示 状态 的 border
    ///   - hover 时 颜色 ++ 10%, press 时 颜色 -- 15%, disabled 时 整体 透明 灰
    ///   - 替代 QuotePanel 中 3 个 Button (开始/取消/恢复默认), 让 BJ 面板 视觉 上一个 台阶.
    ///   - 在 AutoCAD palette 中因为 WinForms System 主题, 默认 FlatStyle.Flat 表现 「平面色块」 较 单薄;
    ///     用 UserPaint + 自 画 渐变/ 高光, 能在 CAD 这种"DPI 杂" 环境 也 能 看出 「玻璃感」。
    /// </summary>
    public class GlassButton : Button
    {
        // ── 颜色 属性 — 可在 new 时 覆盖 ──
        public Color GlassTop { get; set; } = Color.FromArgb(120, 200, 240);   // 顶部 亮色
        public Color GlassBottom { get; set; } = Color.FromArgb(0, 122, 204); // 底部 主色
        public Color BorderColor { get; set; } = Color.FromArgb(0, 80, 160);   // 边线
        public int CornerRadius { get; set; } = 6;                             // 圆角 半 径

        private bool _hover;
        private bool _pressed;

        public GlassButton()
        {
            // 双 缓冲 + UserPaint 必 选 — 避免 重 画 闪
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint |
                ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;
            ForeColor = Color.White;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            Size = new Size(100, 34);
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e) => PaintGlass(e.Graphics);

        private void PaintGlass(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle rect = ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;

            // ── 状态 颜色 ──
            Color top = GlassTop;
            Color bot = GlassBottom;
            float borderAlpha = 0.7f;
            if (!Enabled)
            {
                top = Color.FromArgb(180, top);          // 🔧 v23.1 disabled → alpha=180 (~70% 不透) 表现 真正 灰
                bot = Color.FromArgb(180, bot);
            }
            else if (_pressed)
            {
                top = Darken(GlassTop, 0.15f);
                bot = Darken(GlassBottom, 0.15f);
            }
            else if (_hover)
            {
                top = Lighten(GlassTop, 0.10f);
                bot = Lighten(GlassBottom, 0.10f);
                borderAlpha = 1.0f;
            }

            // ── 主 渐变 填充 ──
            using (var path = RoundedPath(rect, CornerRadius))
            using (var brush = new LinearGradientBrush(rect, top, bot, LinearGradientMode.Vertical))
            {
                g.FillPath(brush, path);

                // 顶部 半透 高光 (模拟 玻璃 反 光) — 只 画 上半 个 rounded rect
                Rectangle hlRect = new Rectangle(rect.X, rect.Y, rect.Width, rect.Height / 2);
                using (var hlPath = RoundedPathTop(hlRect, CornerRadius))
                using (var hlBrush = new LinearGradientBrush(
                    hlRect, Color.FromArgb(110, Color.White), Color.FromArgb(0, Color.White),
                    LinearGradientMode.Vertical))
                {
                    g.FillPath(hlBrush, hlPath);
                }

                // 边线
                Color bd = Color.FromArgb((int)(255 * borderAlpha), BorderColor);
                using (var pen = new Pen(bd, 1))
                {
                    g.DrawPath(pen, path);
                }
            }

            // ── Text — disabled 时 灰 ──
            Color textColor = Enabled ? ForeColor : Color.FromArgb(180, ForeColor);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        private static GraphicsPath RoundedPath(Rectangle rect, int radius)
        {
            int r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
            path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
            path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            return path;
        }

        // 上半 rounded rect — 给 高光 用 (不 要 底部 圆角, 高光 只 在 上部)
        private static GraphicsPath RoundedPathTop(Rectangle rect, int radius)
        {
            int r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
            path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
            path.AddLine(rect.Right, rect.Y + r, rect.Right, rect.Bottom);
            path.AddLine(rect.Right, rect.Bottom, rect.X, rect.Bottom);
            path.AddLine(rect.X, rect.Bottom, rect.X, rect.Y + r);
            path.CloseFigure();
            return path;
        }

        private static Color Lighten(Color c, float amount)
        {
            return Color.FromArgb(c.A,
                Math.Min(255, (int)(c.R + (255 - c.R) * amount)),
                Math.Min(255, (int)(c.G + (255 - c.G) * amount)),
                Math.Min(255, (int)(c.B + (255 - c.B) * amount)));
        }

        private static Color Darken(Color c, float amount)
        {
            return Color.FromArgb(c.A,
                Math.Max(0, (int)(c.R * (1 - amount))),
                Math.Max(0, (int)(c.G * (1 - amount))),
                Math.Max(0, (int)(c.B * (1 - amount))));
        }

        // ── 状态 跟踪 ──
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs mevent) { base.OnMouseDown(mevent); _pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs mevent) { base.OnMouseUp(mevent); _pressed = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    }
}
