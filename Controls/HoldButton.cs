namespace Dezolati.Controls
{
    /// <summary>
    /// Pastelkové tlačítko, které se drží (hry: foukat, nabírat, do pece…). Pod ním obyčejný PencilButton,
    /// nad ním průhledný GraphicsView, který hlásí stisk a puštění (TapGestureRecognizer to neumí).
    /// </summary>
    public class HoldButton : Grid
    {
        private readonly PencilButton _button = new() { InputTransparent = true };
        private readonly GraphicsView _touch = new() { BackgroundColor = Colors.Transparent };

        public event EventHandler? Pressed;
        public event EventHandler? Released;
        public bool IsHeld { get; private set; }

        public string Text { get => _button.Text; set => _button.Text = value; }
        public PencilVariant Variant { get => _button.Variant; set => _button.Variant = value; }

        public HoldButton()
        {
            Children.Add(_button);
            Children.Add(_touch);
            _touch.StartInteraction += (_, _) => Down();
            _touch.EndInteraction += (_, _) => Up();
            _touch.CancelInteraction += (_, _) => Up();
        }

        private void Down()
        {
            if (IsHeld || !IsEnabled) return;
            IsHeld = true;
            _button.Scale = 0.94;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        private void Up()
        {
            if (!IsHeld) return;
            IsHeld = false;
            _button.Scale = 1;
            Released?.Invoke(this, EventArgs.Empty);
        }
    }
}
