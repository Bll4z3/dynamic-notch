# Dynamic Notch 2.1: test checklist

Tick each box, or note what went wrong next to it. Open the notch by hovering the top of the screen;
use ← / → (or the dots) to reach **Notifications + Controls**.

> The notch hides while any app is fullscreen (Settings ▸ General ▸ *Hide notch when an app is fullscreen*).
> If it's missing, un-maximise / exit fullscreen in the app in front, or turn that switch off while testing.

## Already passed (tested by Claude)
- Clear all: cascade, "✓ Cleared", count ticks down, empty state with bell wobble
- Undo within 5 s restores the rows
- × dismiss and drag-to-dismiss (short drag springs back, fling removes)
- Charging colours (simulated): green ring/bolt while charging, white bolt "On battery" pill
- Dragging a page row in Settings ▸ Pages (lift, reflow, saved)

## 1. Notifications history
- [ ] Rows appear as alerts come in (connect headphones, toggle Wi-Fi, etc.)
- [ ] **Clear all** → wait more than 5 s → the rows don't come back (purged)
- [ ] **Clear all** → **Undo** in time → the rows come back in the same order

## 2. Charging (real charger)
- [ ] Unplug: "On battery" pill with a **white** bolt and the %
- [ ] Plug in: "Charging" pill with a **green** bolt
- [ ] While plugged in, the closed notch's ring + bolt are **green** even at a low %
- [ ] Open the notch while charging: the header ring is green too
- [ ] Unplug: the ring fades back to the normal colour (green / amber / red by level)

## 3. Page order (Settings ▸ Pages)
- [ ] Drag a row by the six dots: it lifts, others move aside, it settles
- [ ] Click a row, then press **Tab** until it shows a blue focus ring; **Alt+↑ / Alt+↓** moves it
- [ ] Switched-off pages are dimmed but can still be moved
- [ ] The notch follows straight away: ← / → order, the dots, and the icons when you hover the dots
- [ ] You can't turn off the last page that's on

## 4. Fonts (Settings ▸ General)
- [ ] General is the first tab; Minimal notch / Start with Windows / Hide when fullscreen switches work
- [ ] Hovering a font in the list changes **only** the sample card
- [ ] Choosing a font changes the notch, the pills and Settings immediately (short fade)
- [ ] Check each bundled font: Inter, Geist, DM Sans, Manrope, Plus Jakarta Sans, Outfit, Space Grotesk
- [ ] Check the system fonts listed (only installed ones show)
- [ ] Search under **All installed fonts** finds a font, and picking it works
- [ ] Nothing overflows the notch; the clock digits don't jiggle as seconds/minutes change
- [ ] **Reset to default** goes back to Inter
- [ ] Restart the app: your font choice is kept

## 5. Wi-Fi popup (tile on page 4)
- [ ] Clicking the Wi-Fi tile opens the popup (it springs from the tile); the tile no longer toggles Wi-Fi
- [ ] Networks listed: connected first, then by signal; lock icon on secured ones; "Connected / Saved / Secured / Open"
- [ ] If it says **Network names are hidden**: turn on Settings ▸ Privacy ▸ Location ▸ *Let desktop apps access your location*, then Refresh
- [ ] Switch in the header turns Wi-Fi off ("Wi-Fi is off") and on again
- [ ] Refresh shows a spinner while scanning
- [ ] Click a **saved** network: spinner, then a check, and it becomes Connected
- [ ] Click a **new secured** network: a password box opens in the row; type the password, press Connect
- [ ] A wrong password shakes the row and says so
- [ ] **Disconnect** on the connected row works
- [ ] Esc / clicking the dim area / × closes the popup

## 6. Bluetooth popup (tile on page 4)
- [ ] Clicking the Bluetooth tile opens the popup; the switch in its header turns Bluetooth on/off
- [ ] **My devices** lists paired devices with an icon, Connected / Paired, and a battery ring when known
- [ ] Click a connected **headphones/speaker**: spinner → it disconnects (row updates by itself)
- [ ] Click it again: it reconnects
- [ ] Mice / keyboards show a › and open Bluetooth settings (expected: Windows has no way for apps to connect those)
- [ ] Put a device in pairing mode: it appears under **Nearby**; **Pair** pairs it (Windows may ask you to confirm)
- [ ] Turning a device on/off elsewhere updates its row live

## Report back
For anything that fails, say what you did and what happened, and send a screenshot if you can.
Especially: **which Wi-Fi steps worked**, and **which Bluetooth devices connected/disconnected in the app**.
