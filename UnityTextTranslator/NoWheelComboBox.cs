using System.Windows.Forms;

namespace UnityTextTranslator
{
    /// <summary>Не меняет выбранный пункт колесом мыши, пока список не раскрыт — типичный UX для форм настроек.</summary>
    internal sealed class NoWheelComboBox : ComboBox
    {
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!DroppedDown)
            {
                if (e is HandledMouseEventArgs hm)
                    hm.Handled = true;
                return;
            }

            base.OnMouseWheel(e);
        }
    }
}
