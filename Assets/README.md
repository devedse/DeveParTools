# DeveParTools artwork

A pixel-art shield around a 3×3 grid of data blocks. One block is missing (red dashed outline), and two gold
recovery blocks rebuild it (teal arrow). It uses the same style and palette as the QueueCache icon: teal, gold and
silver on a dark navy background.

| File | Use |
|---|---|
| `logo.png` | Full-size artwork (1254 × 1254), used in the README. |
| `icon.png` | 256 × 256, the desktop app's window icon. |
| `icon.ico` | 16, 24, 32, 48, 64, 128 and 256 px frames, the Windows icon of `deveparui.exe` and `devepar.exe`. |

The artwork is original. It was made with OpenAI Codex's built-in image generation, with the QueueCache icon
given as the style reference, then made into the final files like this:
- The background was set to exactly #101C30: pixels within 9 of that value per colour channel were replaced by it.
- `icon.png` and the `.ico` frames were downscaled with area averaging (ffmpeg `flags=area`). Nearest-neighbour
  broke up the shapes at 16 and 32 px.

Generation prompt: *A cool, nerdy pixel-art application icon for DeveParTools, a PAR2/PAR3 parity archive tool
that verifies files and rebuilds missing data blocks from recovery (parity) blocks. Centered composition: a 3x3 grid
of chunky teal data blocks where the middle-right block is missing, shown as a dashed red outlined gap with a few
pixel cracks; below the grid two gold recovery blocks with a bold teal pixel arrow pointing up into the gap. A subtle
silver pixel shield outline frames the whole grid. Crisp chunky square pixels, flat front view with slight pixel-art
depth. Teal, dark navy, silver and gold, with a single red accent only for the missing block. Opaque solid dark navy
background (#101C30), no text, glow, blur or watermark.*

Edit prompt: *Make the bottom-right teal block complete and intact; the arrow passes behind it, visible below the
block and again above it, where it rises into the red dashed gap.*
