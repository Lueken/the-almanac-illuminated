# The Almanac: Illuminated — Credits

## Technique adaptations

Local-illumination shading (the book lit by the light actually reaching the reader) is
adapted from the **Scribe** mod by RaptorKhan (Nick Edises) —
https://github.com/EdzyWhat/vintagestory-scribe-libgui, MIT License, Copyright (c) 2026
Nick Edises. The three-input sampling model (block grid + ambient + held light), the
non-linear brightness response curve, and the smoothing/flicker handling follow Scribe's
`ScribeAmbientLightSampler` / `ScribeBrightnessCurve`; the application mechanism (a
draw-time shade over the native composer / physical book, in place of Scribe's LibGUI
paint-cache tint) is Illuminated's own. See `source/Gui/AmbientLightSampler.cs` and
`source/Gui/IlluminationCurve.cs` for the per-file notices.
