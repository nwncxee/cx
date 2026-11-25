-- The CX settings an area may hold, one entry per feature. Each mirrors a knob group the client
-- declares (managed/, KnobGroup.Declare) and lets a server hold: key, range and default come from
-- that declaration, so keep them in step with it. `cx.<feature>.<key>` is the variable's name.
-- A feature's quality preset is left out: it follows the player's hardware, not the area's look.
--
-- kind: "check" (an int 0/1), "number" (a float), "choice" (a string from `choices`).
-- master: the feature's switch. The form offers it as "the values below" / `off` (default "No <title>").

return {
    {
        id = "vfog",
        title = "Volumetric fog",
        off = "Classic fog (no volumetric fog)",
        settings = {
            { key = "enabled", label = "Volumetric fog", kind = "check", default = true, master = true },
            { key = "haze", label = "Haze in the air", kind = "number", min = 0, max = 0.9, default = 0 },
            { key = "sun", label = "Sun shafts", kind = "number", min = 0, max = 1, default = 0.5 },
            { key = "lights", label = "Lamp glow", kind = "number", min = 0, max = 4, default = 0.15 },
            { key = "reach", label = "Lamp strength by reach", kind = "number", min = 0, max = 1, default = 0.1 },
            { key = "sky", label = "Fog takes the sky colour", kind = "number", min = 0, max = 1, default = 0.5 },
            { key = "aniso", label = "Forward scattering", kind = "number", min = 0, max = 0.9, default = 0.4 },
            { key = "height", label = "Haze height falloff (/m)", kind = "number", min = 0, max = 0.2, default = 0.08 },
            { key = "floor", label = "Haze floor (m)", kind = "number", min = -20, max = 50, default = 0 },
            { key = "noise", label = "Patchiness", kind = "number", min = 0, max = 1, default = 0.6 },
            { key = "noisesize", label = "Patch size (m)", kind = "number", min = 2, max = 64, default = 16 },
            { key = "wind", label = "Drift speed (m/s)", kind = "number", min = 0, max = 8, default = 1.5 },
            { key = "top", label = "Fog bank height (m)", kind = "number", min = 2, max = 100, default = 15 },
            { key = "temporal", label = "Frames blended", kind = "number", min = 0, max = 0.98, default = 0.9 },
            { key = "lightshadows", label = "Lamp shadows in fog", kind = "check", default = true },
        },
    },
}
