namespace Aimmy.Mac
{
    public struct RectF
    {
        public float X, Y, Width, Height;
        public RectF(float x, float y, float w, float h)
        {
            X = x; Y = y; Width = w; Height = h;
        }
        public override string ToString() => $"[X={X}, Y={Y}, W={Width}, H={Height}]";
    }

    public class Prediction
    {
        public string Label { get; set; } = "Target";
        public RectF Rectangle { get; set; }
        public float Confidence { get; set; }
        public int ClassId { get; set; } = 0;
        public string ClassName { get; set; } = "Enemy";
        public float CenterXTranslated { get; set; }
        public float CenterYTranslated { get; set; }
        public float ScreenCenterX { get; set; }
        public float ScreenCenterY { get; set; }
    }
}
