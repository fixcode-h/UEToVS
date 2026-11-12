using System;

namespace UEToVS.UI.Logic
{
    /// <summary>Pointer state of a toggle switch (keyboard focus is drawn separately by the focus ring).</summary>
    public enum ToggleSwitchPointer
    {
        Rest = 0,
        Hover = 1,
        Pressed = 2,
    }

    /// <summary>Knob rectangle inside the track, in DIPs relative to the track's top-left corner.</summary>
    public readonly struct KnobRect
    {
        public KnobRect(double x, double y, double width, double height)
        {
            X = x; Y = y; Width = width; Height = height;
        }

        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }
        public double Right => X + Width;
        public double CenterY => Y + Height / 2;
        /// <summary>Corner radius that keeps the knob a circle / stadium.</summary>
        public double Radius => Math.Min(Width, Height) / 2;

        public override string ToString() => $"({X:0.##},{Y:0.##} {Width:0.##}x{Height:0.##})";
    }

    /// <summary>Token names (see <see cref="TokenNames"/>) a toggle switch paints with in one state.</summary>
    public readonly struct ToggleSwitchColors
    {
        public ToggleSwitchColors(string fill, string stroke, string knob)
        {
            Fill = fill; Stroke = stroke; Knob = knob;
        }

        /// <summary>Track fill token.</summary>
        public string Fill { get; }
        /// <summary>Track border token, or null for no border (the "on" track is borderless).</summary>
        public string Stroke { get; }
        public string Knob { get; }
    }

    /// <summary>
    /// Windows 11 / VS 2026 (Fluent) ToggleSwitch geometry, WPF-free so it is unit tested. Track 40x20, fully
    /// rounded. Knob 12x12 at rest (4px inset), 14x14 on hover, 17x14 while pressed (stretched toward the travel
    /// direction, the anchored edge stays put). <c>progress</c> is the knob travel: 0 = off (left), 1 = on (right);
    /// the WPF control animates it.
    /// </summary>
    public static class ToggleSwitchGeometry
    {
        public const double TrackWidth = 40;
        public const double TrackHeight = 20;
        public const double TrackRadius = TrackHeight / 2;
        public const double TrackStroke = 1;
        public const double KnobRest = 12;
        public const double KnobHover = 14;
        public const double KnobPressedWidth = 17;
        /// <summary>Gap between label text and the switch (Fluent ToggleSwitch content spacing).</summary>
        public const double ContentGap = 12;
        /// <summary>Knob travel animation length used when animations are on (Fluent "fast" duration).</summary>
        public const int AnimationMs = 120;

        public static (double Width, double Height) KnobSize(ToggleSwitchPointer pointer, bool enabled)
        {
            if (!enabled) return (KnobRest, KnobRest);
            switch (pointer)
            {
                case ToggleSwitchPointer.Hover: return (KnobHover, KnobHover);
                case ToggleSwitchPointer.Pressed: return (KnobPressedWidth, KnobHover);
                default: return (KnobRest, KnobRest);
            }
        }

        /// <summary>
        /// Knob rectangle for a travel <paramref name="progress"/> (clamped to 0..1). The inset is (track height − knob
        /// height) / 2 on every side, so the knob stays vertically centred and a wider pressed knob grows away from
        /// the edge it rests against.
        /// </summary>
        public static KnobRect Knob(double progress, ToggleSwitchPointer pointer, bool enabled)
        {
            if (double.IsNaN(progress)) progress = 0;
            progress = Math.Max(0, Math.Min(1, progress));
            var (w, h) = KnobSize(pointer, enabled);
            double inset = (TrackHeight - h) / 2;
            double offX = inset;
            double onX = TrackWidth - inset - w;
            double x = offX + (onX - offX) * progress;
            return new KnobRect(x, inset, w, h);
        }

        /// <summary>
        /// Fluent ease-out ("decelerate") curve used for the knob travel: cubic-bezier(0, 0, 0, 1) approximated by
        /// 1 − (1 − t)^3. Monotonic, f(0) = 0, f(1) = 1.
        /// </summary>
        public static double EaseOut(double t)
        {
            if (double.IsNaN(t) || t <= 0) return 0;
            if (t >= 1) return 1;
            double u = 1 - t;
            return 1 - u * u * u;
        }

        /// <summary>
        /// Duration of the knob travel: 0 when either the system (SystemParameters.ClientAreaAnimation) or the
        /// UEToVS appearance settings turned animations off; otherwise <see cref="AnimationMs"/>.
        /// </summary>
        public static int TravelMs(bool systemAnimations, double appearanceAnimationMs) =>
            systemAnimations && appearanceAnimationMs > 0 ? AnimationMs : 0;

        /// <summary>Token roles for a state: off = outline + stroke-coloured knob, on = accent fill + on-accent knob.</summary>
        public static ToggleSwitchColors Colors(bool isOn, ToggleSwitchPointer pointer, bool enabled)
        {
            if (!enabled)
                return isOn
                    ? new ToggleSwitchColors(TokenNames.ToggleTrackOnDisabled, null, TokenNames.ToggleThumbDisabled)
                    : new ToggleSwitchColors(TokenNames.ToggleTrackOff, TokenNames.ToggleStrokeDisabled, TokenNames.ToggleStrokeDisabled);
            if (isOn)
            {
                string fill = pointer == ToggleSwitchPointer.Pressed ? TokenNames.AccentPressed
                    : pointer == ToggleSwitchPointer.Hover ? TokenNames.AccentHover
                    : TokenNames.Accent;
                return new ToggleSwitchColors(fill, null, TokenNames.ToggleThumb);
            }
            string offFill = pointer == ToggleSwitchPointer.Pressed ? TokenNames.ToggleTrackOffPressed
                : pointer == ToggleSwitchPointer.Hover ? TokenNames.ToggleTrackOffHover
                : TokenNames.ToggleTrackOff;
            return new ToggleSwitchColors(offFill, TokenNames.ToggleStroke, TokenNames.ToggleStroke);
        }
    }
}
