# Changelog

Generated from `releases/notes.json`. Unreleased changes are not shipped.

## Unreleased

### Added

- A hands-on tutorial on Mac and Windows. Ten quick steps: grab a tool, draw a box, change the color, draw a curved arrow, circle something with the pen, move it, undo, go back to your apps, draw again, and hide and show everything. A ghost cursor shows each step, keys show right in the instructions, and shortcuts light up on a little keyboard. Use the keys or the mouse, whichever you like. Finishing pops a big burst of confetti. Replay it from Settings > Help.

### Changed

- The toolbar now shows all properties by default, so options like Curved are easy to find. Focus and cleanup and Zoom stay on the toolbar with them. Quick controls is still one click away, with its own one-slider icon.

### Removed

- Removed the four-page Quick start tour. The hands-on tutorial replaces it.

## 0.1.0-beta.12 (build 30) - 2026-10-23

### Changed

- Annotator is now open source under the Apache License 2.0. The code for the Mac and Windows apps is on GitHub at github.com/dorlugasigal/annotator. Settings > About shows the license on both platforms.

### Removed

- Removed unused licensing code. All tools stay free, with no account or key.

### Known limits

- The break timer's sound while the computer is locked and its return after sign-in were checked by the owner on the development build only.
- The animations added in beta.7 passed automated checks but have not yet been watched by a person on the real desktop. Tell us if any style looks wrong or feels slow.
- The new names of the three toolbar menus were checked in the app's accessibility tree. A person still needs to listen to them with VoiceOver.
- The screen zoom ramp was checked with automated state tests only. The live-capture zoom tests did not run on the validation Mac because the test build had no Screen Recording permission.
- In the Shared Region window, the spotlight still turns on and off at once instead of fading.
- Shared animations passed controlled local capture checks. Their appearance in a Teams, Meet or Zoom receiver still needs a hands-on check.
- The controlled toolbar-sharing check has been inconsistent on this Mac. Check that the toolbar appears in your meeting's shared window before relying on it.
- The owner confirmed the region highlight works on the current monitor setup. Full-screen browser transitions, Stage Manager and unplugging or reconnecting monitors still need separate checks.
- Choosing a different area in the configuration window or changing the display setup turns region sharing off. Check the outline before starting it again.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- The Intel app and tests compiled, but runtime tests could not run because Rosetta is missing on the validation Mac. No Intel Mac has run this version yet.
- Installing on another Mac and updating from an earlier version still need a hands-on check.
- VoiceOver and Full Keyboard Access checks remain incomplete.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Cross-monitor dragging still needs a hands-on check.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.

## 0.1.0-beta.11 (build 29) - 2026-10-22

### Mac

#### Added

- A break timer shows a big countdown on every screen, so everyone knows when to come back. Start it from the menu-bar menu on a Mac or the tray menu on Windows, or press Control-3 (Ctrl+3 on Windows). Choose its length, title, colors and background in Settings > Break timer.
- The break timer can lock your computer as soon as someone presses a key, clicks or moves the mouse. macOS and Windows don't let apps show anything on the lock screen, so the timer stays on screen until then. While your computer is locked, the timer keeps counting and plays its sound at zero. It comes back when you sign in.

#### Known limits

- The break timer's sound while the computer is locked and its return after sign-in were checked by the owner on the development build only.
- The animations added in beta.7 passed automated checks but have not yet been watched by a person on the real desktop. Tell us if any style looks wrong or feels slow.
- The new names of the three toolbar menus were checked in the app's accessibility tree. A person still needs to listen to them with VoiceOver.
- The screen zoom ramp was checked with automated state tests only. The live-capture zoom tests did not run on the validation Mac because the test build had no Screen Recording permission.
- In the Shared Region window, the spotlight still turns on and off at once instead of fading.
- Shared animations passed controlled local capture checks. Their appearance in a Teams, Meet or Zoom receiver still needs a hands-on check.
- The controlled toolbar-sharing check has been inconsistent on this Mac. Check that the toolbar appears in your meeting's shared window before relying on it.
- The owner confirmed the region highlight works on the current monitor setup. Full-screen browser transitions, Stage Manager and unplugging or reconnecting monitors still need separate checks.
- Choosing a different area in the configuration window or changing the display setup turns region sharing off. Check the outline before starting it again.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- The Intel app and tests compiled, but runtime tests could not run because Rosetta is missing on the validation Mac. No Intel Mac has run this version yet.
- Installing on another Mac and updating from an earlier version still need a hands-on check.
- VoiceOver and Full Keyboard Access checks remain incomplete.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Cross-monitor dragging still needs a hands-on check.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.

### Windows

#### Added

- You can now install Annotator without the Microsoft Store. Download the ZIP for your PC (x64 or ARM64) from the release page, unzip it and open Annotator.exe. It doesn't need administrator rights. To update it, download the new ZIP, or run winget upgrade DorLugasiGal.Annotator once Annotator is in winget.

#### Known limits

- The ZIP isn't code-signed yet, so Windows may show a SmartScreen warning when you open it. Choose More info, then Run anyway, only if you downloaded it from the Annotator release page.
- The ZIP version doesn't add a Start menu entry. Start it from the folder you unzipped it to, or with the annotator command after installing it with winget. Turn on Launch at sign-in in Settings > General to start it with Windows.
- Ctrl+1, Ctrl+2 and Ctrl+3 replace tab switching in browsers, and those keys in other apps, while Annotator runs. Change them in Settings > Toolbar & Shortcuts.
- Windows' screen access questions still need a hands-on check, including that no yellow border appears around the screen during zoom, effects or region sharing.
- Narrator, keyboard-only use, contrast themes and large text passed automated checks. A person still needs to try them.
- Typing with Japanese, Chinese and Korean input methods still needs a hands-on check.
- Pen pressure and touch drawing still need a check with a real pen and touch screen.
- How drawings, zoom, effects and the shared region look in Teams, Zoom and Google Meet still needs a hands-on check.
- Two displays with different scaling, and unplugging or reconnecting a display, still need a hands-on check.
- The ARM64 version has run only in a Windows 11 on Arm virtual machine, not yet on an ARM64 PC.
- While Draw is on, Annotator is the active window, so the app you present looks inactive. On a Mac, that app stays active.
- Start, notifications, Alt+Tab, Task View, Snap layouts, the touch keyboard, administrator prompts and the lock screen can appear above your drawings.
- While an app that runs as administrator is in front, Alt+scroll and Escape don't reach Annotator. Use the zoom controls on the toolbar instead.
- Windows may hide the tray icon under Show hidden icons, the arrow next to the clock. To keep it visible, use Windows Settings > Personalization > Taskbar > Other system tray icons.
- Uninstalling Annotator removes its settings and My shapes. Boards you saved stay where you saved them.
- Games that use exclusive full screen may hide your drawings.
- Text uses Segoe UI Variable and Cascadia Mono instead of the Mac's fonts, so letters look slightly different and text boxes are a little taller.
- Icons come from the Lucide set instead of Apple's SF Symbols, so some look slightly different.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- Effects do not follow moving windows or scrolling content and are not secure redaction.

## 0.1.0-beta.10 (build 28) - 2026-10-21

### Mac

#### Changed

- This version changes only the Windows edition. The Mac app is the same as in beta.9.

#### Known limits

- The animations added in beta.7 passed automated checks but have not yet been watched by a person on the real desktop. Tell us if any style looks wrong or feels slow.
- The new names of the three toolbar menus were checked in the app's accessibility tree. A person still needs to listen to them with VoiceOver.
- The screen zoom ramp was checked with automated state tests only. The live-capture zoom tests did not run on the validation Mac because the test build had no Screen Recording permission.
- In the Shared Region window, the spotlight still turns on and off at once instead of fading.
- Shared animations passed controlled local capture checks. Their appearance in a Teams, Meet or Zoom receiver still needs a hands-on check.
- The controlled toolbar-sharing check has been inconsistent on this Mac. Check that the toolbar appears in your meeting's shared window before relying on it.
- The owner confirmed the region highlight works on the current monitor setup. Full-screen browser transitions, Stage Manager and unplugging or reconnecting monitors still need separate checks.
- Choosing a different area in the configuration window or changing the display setup turns region sharing off. Check the outline before starting it again.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- The Intel app and tests compiled, but runtime tests could not run because Rosetta is missing on the validation Mac. No Intel Mac has run this version yet.
- Installing on another Mac and updating from an earlier version still need a hands-on check.
- VoiceOver and Full Keyboard Access checks remain incomplete.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Cross-monitor dragging still needs a hands-on check.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.

### Windows

#### Fixed

- Dragging the toolbar quickly no longer makes it jump around the screen. It now stays under the pointer.

#### Known limits

- Windows' screen access questions still need a hands-on check, including that no yellow border appears around the screen during zoom, effects or region sharing.
- Narrator, keyboard-only use, contrast themes and large text passed automated checks. A person still needs to try them.
- Typing with Japanese, Chinese and Korean input methods still needs a hands-on check.
- Pen pressure and touch drawing still need a check with a real pen and touch screen.
- How drawings, zoom, effects and the shared region look in Teams, Zoom and Google Meet still needs a hands-on check.
- Two displays with different scaling, and unplugging or reconnecting a display, still need a hands-on check.
- The ARM64 version has run only in a Windows 11 on Arm virtual machine, not yet on an ARM64 PC.
- While Draw is on, Annotator is the active window, so the app you present looks inactive. On a Mac, that app stays active.
- Start, notifications, Alt+Tab, Task View, Snap layouts, the touch keyboard, administrator prompts and the lock screen can appear above your drawings.
- While an app that runs as administrator is in front, Alt+scroll and Escape don't reach Annotator. Use the zoom controls on the toolbar instead.
- Windows may hide the tray icon under Show hidden icons, the arrow next to the clock. To keep it visible, use Windows Settings > Personalization > Taskbar > Other system tray icons.
- Uninstalling Annotator removes its settings and My shapes. Boards you saved stay where you saved them.
- Games that use exclusive full screen may hide your drawings.
- Text uses Segoe UI Variable and Cascadia Mono instead of the Mac's fonts, so letters look slightly different and text boxes are a little taller.
- Icons come from the Lucide set instead of Apple's SF Symbols, so some look slightly different.
- Ctrl+1 and Ctrl+2 replace tab switching in browsers, and those keys in other apps, while Annotator runs. Change them in Settings > Toolbar & Shortcuts.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- Effects do not follow moving windows or scrolling content and are not secure redaction.

## 0.1.0-beta.9 (build 27) - 2026-10-21

### Mac

#### Added

- Annotator is now also available for Windows, from the Microsoft Store. The website and Help link to it and give the winget command to install it.

#### Known limits

- The animations added in beta.7 passed automated checks but have not yet been watched by a person on the real desktop. Tell us if any style looks wrong or feels slow.
- The new names of the three toolbar menus were checked in the app's accessibility tree. A person still needs to listen to them with VoiceOver.
- The screen zoom ramp was checked with automated state tests only. The live-capture zoom tests did not run on the validation Mac because the test build had no Screen Recording permission.
- In the Shared Region window, the spotlight still turns on and off at once instead of fading.
- Shared animations passed controlled local capture checks. Their appearance in a Teams, Meet or Zoom receiver still needs a hands-on check.
- The controlled toolbar-sharing check has been inconsistent on this Mac. Check that the toolbar appears in your meeting's shared window before relying on it.
- The owner confirmed the region highlight works on the current monitor setup. Full-screen browser transitions, Stage Manager and unplugging or reconnecting monitors still need separate checks.
- Choosing a different area in the configuration window or changing the display setup turns region sharing off. Check the outline before starting it again.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- The Intel app and tests compiled, but runtime tests could not run because Rosetta is missing on the validation Mac. No Intel Mac has run this version yet.
- Installing on another Mac and updating from an earlier version still need a hands-on check.
- VoiceOver and Full Keyboard Access checks remain incomplete.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Cross-monitor dragging still needs a hands-on check.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.

### Windows

#### Changed

- A right click on the tray icon now opens the same Annotator panel as a left click, instead of a plain Windows menu.

#### Fixed

- Annotator no longer closes suddenly a few seconds after you start full-screen zoom and move the pointer.
- With Cursor halo on, clicks no longer leave broken copies of the halo on the screen.
- If you press a key or click while drawing tools are opening, Annotator still takes keyboard focus. Before, it showed an error that the drawing canvas could not take keyboard focus.

#### Known limits

- Windows' screen access questions still need a hands-on check, including that no yellow border appears around the screen during zoom, effects or region sharing.
- Narrator, keyboard-only use, contrast themes and large text passed automated checks. A person still needs to try them.
- Typing with Japanese, Chinese and Korean input methods still needs a hands-on check.
- Pen pressure and touch drawing still need a check with a real pen and touch screen.
- How drawings, zoom, effects and the shared region look in Teams, Zoom and Google Meet still needs a hands-on check.
- Two displays with different scaling, and unplugging or reconnecting a display, still need a hands-on check.
- The ARM64 version has run only in a Windows 11 on Arm virtual machine, not yet on an ARM64 PC.
- While Draw is on, Annotator is the active window, so the app you present looks inactive. On a Mac, that app stays active.
- Start, notifications, Alt+Tab, Task View, Snap layouts, the touch keyboard, administrator prompts and the lock screen can appear above your drawings.
- While an app that runs as administrator is in front, Alt+scroll and Escape don't reach Annotator. Use the zoom controls on the toolbar instead.
- Windows may hide the tray icon under Show hidden icons, the arrow next to the clock. To keep it visible, use Windows Settings > Personalization > Taskbar > Other system tray icons.
- Uninstalling Annotator removes its settings and My shapes. Boards you saved stay where you saved them.
- Games that use exclusive full screen may hide your drawings.
- Text uses Segoe UI Variable and Cascadia Mono instead of the Mac's fonts, so letters look slightly different and text boxes are a little taller.
- Icons come from the Lucide set instead of Apple's SF Symbols, so some look slightly different.
- Ctrl+1 and Ctrl+2 replace tab switching in browsers, and those keys in other apps, while Annotator runs. Change them in Settings > Toolbar & Shortcuts.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- Effects do not follow moving windows or scrolling content and are not secure redaction.

## 0.1.0-beta.8 (build 26) - 2026-10-20

### Mac

#### Changed

- Save AI skill... now saves a skill that also explains how to use Annotator on Windows.

#### Fixed

- VoiceOver now names three toolbar menus Selection actions, Focus and cleanup, and Zoom tools. It used to read the names of their icons, such as "zoom in".

#### Known limits

- The animations added in beta.7 passed automated checks but have not yet been watched by a person on the real desktop. Tell us if any style looks wrong or feels slow.
- The new names of the three toolbar menus were checked in the app's accessibility tree. A person still needs to listen to them with VoiceOver.
- The screen zoom ramp was checked with automated state tests only. The live-capture zoom tests did not run on the validation Mac because the test build had no Screen Recording permission.
- In the Shared Region window, the spotlight still turns on and off at once instead of fading.
- Shared animations passed controlled local capture checks. Their appearance in a Teams, Meet or Zoom receiver still needs a hands-on check.
- The controlled toolbar-sharing check has been inconsistent on this Mac. Check that the toolbar appears in your meeting's shared window before relying on it.
- The owner confirmed the region highlight works on the current monitor setup. Full-screen browser transitions, Stage Manager and unplugging or reconnecting monitors still need separate checks.
- Choosing a different area in the configuration window or changing the display setup turns region sharing off. Check the outline before starting it again.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- The Intel app and tests compiled, but runtime tests could not run because Rosetta is missing on the validation Mac. No Intel Mac has run this version yet.
- Installing on another Mac and updating from an earlier version still need a hands-on check.
- VoiceOver and Full Keyboard Access checks remain incomplete.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Cross-monitor dragging still needs a hands-on check.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.

### Windows

#### Added

- This is the first Windows version of Annotator. It has the Mac app's tools, boards, settings and words, and runs on Windows 11 version 22H2 or later, on x64 and ARM64 PCs.
- Annotator lives in the system tray, near the clock. Click its icon to open the Annotator menu, or right-click it for a Windows menu with the same actions.
- Start and Windows Settings list the app as Annotator – Screen Annotation, its Microsoft Store name. The app itself is still called Annotator.
- The shortcuts use Ctrl: Ctrl+Shift+2 resumes drawing, Ctrl+2 shows or hides all drawings, Ctrl+1 zooms the screen and Ctrl+Alt+Shift+2 hides only. On a canvas, Ctrl replaces Command and Alt+scroll changes the zoom.
- Annotator needs no Windows permission to draw or use shortcuts. Features that read the screen, such as zoom, Blur, Pixelate, Distort and region sharing, first ask Windows for screen access.
- Launch at sign-in in Settings > General starts Annotator in the tray when you sign in to Windows, without opening the toolbar.
- Double-click an .annotator board in File Explorer to open it in Annotator.

#### Known limits

- Annotator is not in the Microsoft Store yet. This test package is unsigned, so it installs only on a PC with Developer Mode on, and it does not update itself.
- Windows' screen access questions still need a hands-on check, including that no yellow border appears around the screen during zoom, effects or region sharing.
- Narrator, keyboard-only use, contrast themes and large text passed automated checks. A person still needs to try them.
- Typing with Japanese, Chinese and Korean input methods still needs a hands-on check.
- Pen pressure and touch drawing still need a check with a real pen and touch screen.
- How drawings, zoom, effects and the shared region look in Teams, Zoom and Google Meet still needs a hands-on check.
- Two displays with different scaling, and unplugging or reconnecting a display, still need a hands-on check.
- The ARM64 version was built but has not run on an ARM64 PC yet.
- While Draw is on, Annotator is the active window, so the app you present looks inactive. On a Mac, that app stays active.
- Start, notifications, Alt+Tab, Task View, Snap layouts, the touch keyboard, administrator prompts and the lock screen can appear above your drawings.
- While an app that runs as administrator is in front, Alt+scroll and Escape don't reach Annotator. Use the zoom controls on the toolbar instead.
- Windows may hide the tray icon under Show hidden icons, the arrow next to the clock. To keep it visible, use Windows Settings > Personalization > Taskbar > Other system tray icons.
- Uninstalling Annotator removes its settings and My shapes. Boards you saved stay where you saved them.
- Games that use exclusive full screen may hide your drawings.
- Text uses Segoe UI Variable and Cascadia Mono instead of the Mac's fonts, so letters look slightly different and text boxes are a little taller.
- Icons come from the Lucide set instead of Apple's SF Symbols, so some look slightly different.
- Ctrl+1 and Ctrl+2 replace tab switching in browsers, and those keys in other apps, while Annotator runs. Change them in Settings > Toolbar & Shortcuts.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- Effects do not follow moving windows or scrolling content and are not secure redaction.

## 0.1.0-beta.7 (build 25) - 2026-10-13

### Added

- Three new ways to hide and show drawings: Pop, Drop and Soft focus. Choose one in Settings > General > Workspace animation.
- Screen zoom now glides in and out instead of jumping, and the pointer lens fades in.
- Smart Draw now morphs your rough stroke into the recognized shape.
- Erased drawings now fade out, and the spotlight fades in and out.

### Changed

- Hide and show animations start moving sooner and land more smoothly. Reversing one halfway now takes less time.
- Magic is faster and calmer. Each shape makes one smooth swoop instead of weaving back and forth.
- Gather keeps shapes at full size longer, and the toolbar pulses as each shape arrives. Shapes closest to the toolbar leave first.
- Dissolve now breaks up from the edge closest to the toolbar, and its particles slow down as they spread.
- With macOS Reduce Motion on, a short fade now replaces movement instead of cutting straight to the result.
- The selected tool highlight now slides between tools, and the Draw/Apps button changes smoothly.
- Tips on screen, the Use apps button and newly opened tool properties now fade in.
- The Pulse click effect now rises quickly and settles slowly, and click effects follow your display's refresh rate.

### Known limits

- The new animations passed automated checks but have not yet been watched by a person on the real desktop. Tell us if any style looks wrong or feels slow.
- The screen zoom ramp was checked with automated state tests only. The live-capture zoom tests did not run on the validation Mac because the test build had no Screen Recording permission.
- In the Shared Region window, the spotlight still turns on and off at once instead of fading.
- Shared animations passed controlled local capture checks. Their appearance in a Teams, Meet or Zoom receiver still needs a hands-on check.
- The controlled toolbar-sharing check has been inconsistent on this Mac. Check that the toolbar appears in your meeting's shared window before relying on it.
- The owner confirmed the region highlight works on the current monitor setup. Full-screen browser transitions, Stage Manager and unplugging or reconnecting monitors still need separate checks.
- Choosing a different area in the configuration window or changing the display setup turns region sharing off. Check the outline before starting it again.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- The Intel app and tests compiled, but runtime tests could not run because Rosetta is missing on the validation Mac. No Intel Mac has run this version yet.
- Installing on another Mac and updating from an earlier version still need a hands-on check.
- VoiceOver and Full Keyboard Access checks remain incomplete.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Cross-monitor dragging still needs a hands-on check.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.

## 0.1.0-beta.6 (build 24) - 2026-10-13

### Changed

- Covers and pixel effects stay in place during shared animations. They appear immediately when showing drawings and remain until hiding finishes.
- Wait for a running workspace animation to finish before starting region sharing.

### Fixed

- Showing or hiding the workspace now animates in the Shared Region window, including when that window is behind other apps.
- Shared animations follow the desktop timing and direction, including when you reverse them.

### Known limits

- Shared animations passed controlled local capture checks. Their appearance in a Teams, Meet or Zoom receiver still needs a hands-on check.
- The controlled toolbar-sharing check has been inconsistent on this Mac. Check that the toolbar appears in your meeting's shared window before relying on it.
- The owner confirmed the region highlight works on the current monitor setup. Full-screen browser transitions, Stage Manager and unplugging or reconnecting monitors still need separate checks.
- Choosing a different area in the configuration window or changing the display setup turns region sharing off. Check the outline before starting it again.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- The Intel app and tests compiled, but runtime tests could not run because Rosetta is missing on the validation Mac. No Intel Mac has run this version yet.
- Installing on another Mac and updating from an earlier version still need a hands-on check.
- VoiceOver and Full Keyboard Access checks remain incomplete.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Cross-monitor dragging still needs a hands-on check.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.

## 0.1.0-beta.5 (build 23) - 2026-10-13

### Changed

- Region guides can appear in desktop screenshots and other apps' full-screen shares. They stay out of Annotator's Shared Region window.

### Fixed

- Fixed region highlights missing from external displays when choosing a sharing area.
- Region guides are created on the selected display from the start, rather than first opening outside the connected screens.

### Known limits

- The owner confirmed the region highlight works on the current monitor setup. Full-screen browser transitions, Stage Manager and unplugging or reconnecting monitors still need separate checks.
- The controlled toolbar-sharing check has been inconsistent on this Mac. Check that the toolbar appears in your meeting's shared window before relying on it.
- The receiver's view in Teams, Meet and Zoom still needs a hands-on check.
- Choosing a different area in the configuration window or changing the display setup turns region sharing off. Check the outline before starting it again.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- The Intel app and tests compiled, but runtime tests could not run because Rosetta is missing on the validation Mac. No Intel Mac has run this version yet.
- Installing on another Mac and updating from an earlier version still need a hands-on check.
- VoiceOver and Full Keyboard Access checks remain incomplete.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Cross-monitor dragging still needs a hands-on check.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.

## 0.1.0-beta.4 (build 22) - 2026-10-13

### Fixed

- The region preview and custom-region selection stay above app windows on every display. Both can appear in other apps' full-screen spaces.

### Known limits

- Region selection across displays passed automated local tests. Full-screen browser transitions, Stage Manager, physical cursor movement and monitor unplug/replug still need a hands-on check.
- The controlled toolbar-sharing check did not pass consistently on this Mac, including with previous source. Check that the toolbar appears in your meeting's shared window before relying on it.
- The receiver's view in Teams, Meet and Zoom still needs a hands-on check.
- Choosing a different area in the configuration window or changing the display setup turns region sharing off. Check the outline before starting it again.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- The Intel app and tests compiled, but runtime tests could not run because Rosetta is missing on the validation Mac. No Intel Mac has run this version yet.
- Installing on another Mac and updating from an earlier version still need a hands-on check.
- VoiceOver and Full Keyboard Access checks remain incomplete.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Cross-monitor dragging still needs a hands-on check.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.

## 0.1.0-beta.3 (build 21) - 2026-10-13

### Fixed

- The region preview now creates its outline on the selected display when you switch screens.
- Draw a custom region now works on any connected display, with a crosshair on every screen. The drag's starting display becomes the source. Escape cancels without changing the previous region.

### Known limits

- Region selection across displays passed automated local tests. Physical dual-monitor cursor movement, separate Spaces and monitor unplug/replug still need a hands-on check.
- The controlled toolbar-sharing check did not pass consistently on this Mac, including with the previous source. Check that the toolbar appears in your meeting's shared window before relying on it.
- Region sharing passed controlled local tests. The receiver's view in Teams, Meet and Zoom still needs a hands-on check.
- Choosing a different area in the configuration window or changing the display setup turns region sharing off. Check the outline before starting it again.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- The Intel app and tests compiled, but runtime tests could not run because Rosetta is missing on the validation Mac. No Intel Mac has run this version yet.
- Installing on another Mac and updating from an earlier version still need a hands-on check.
- VoiceOver and Full Keyboard Access checks remain incomplete.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Cross-monitor dragging still needs a hands-on check.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.

## 0.1.0-beta.2 (build 20) - 2026-10-12

### Added

- Share the left or right half of your display straight from Annotator's menu. Configure region opens the full chooser.
- A small red dot beside the menu-bar icon shows that region sharing is on. The Stop action also turns red.

### Changed

- The visible toolbar now appears in the shared region. Hiding it or moving it outside the region removes it from the shared view.
- The shared window opens behind your other windows without taking focus, and keeps updating when covered. Keep it open for your meeting app; minimizing it is not a supported way to hide it.
- The website's ultrawide section now explains sharing part of your screen to keep text readable.

### Fixed

- The faint region fill now appears only while choosing an area. Starting region sharing clears the fill and keeps the outline.
- Control-2 now hides drawings, covers, text, images, board backgrounds and effects in the shared view too. The region keeps running. Showing drawings restores them.

### Known limits

- Region sharing passed controlled local tests. The receiver's view in Teams, Meet and Zoom still needs a hands-on check.
- Choosing a different area in the configuration window or changing the display setup turns region sharing off. Check the outline before starting it again.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- The Intel app and tests compiled, but runtime tests could not run because Rosetta is missing on the validation Mac. No Intel Mac has run this version yet.
- Installing on another Mac and updating from an earlier version still need a hands-on check.
- VoiceOver and Full Keyboard Access checks remain incomplete.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Cross-monitor dragging and monitor unplug/replug still need a hands-on check.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.

## 0.1.0-beta.1 (build 19) - 2026-10-12

### Added

- Share region opens one window showing part of your desktop. Choose a half, third or corner, or draw a custom rectangle. A faint fill and outline show the area before you start. Share the Annotator - Shared Region window in your meeting app to show the apps inside it.
- Start and stop region sharing from the menu or its wider configuration window. On and Off show whether it is running. Hiding the toolbar keeps it on. Drawings, text, the pointer and live effects appear in the output; the toolbar and region outline stay out.
- The website shows the real region chooser switching between presets, with a short guide for sharing from an ultrawide screen.

### Fixed

- Double-clicking empty space now uses your saved Text tool settings, including font, size, color and alignment. The text properties show those settings as soon as you start typing.
- The region outline appears when you open its settings from the menu, even with the toolbar hidden.

### Known limits

- Region sharing passed controlled local tests. The receiver's view in Teams, Meet and Zoom still needs a hands-on check.
- Changing the region or the display setup turns region sharing off. Check the outline before starting it again.
- Anything inside a shared region can appear, including notifications. Remove private details before sharing; blur is not secure redaction.
- The Intel build compiled, but this version's Intel runtime tests could not run because Rosetta is missing on the validation Mac. No Intel Mac has run this version yet.
- Installing on another Mac and updating from an earlier version still need a hands-on check.
- VoiceOver and Full Keyboard Access checks remain incomplete.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Cross-monitor dragging and monitor unplug/replug still need a hands-on check.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.

## 0.1.0-alpha.10 (build 18) - 2026-10-10

### Changed

- A selected arrow or line no longer has a box around it. Its point handles show that it is selected. Locked arrows and groups still show their outline.

### Fixed

- You can now drag selected drawings from one monitor to another. Undo moves them back, and locked objects stay where they are.
- If you disconnect the monitor that held the toolbar, it now returns to a connected monitor. Showing the tools again no longer leaves the toolbar off screen.
- The website reel now waits for video data before starting in Safari. Play and Pause still work if automatic playback is blocked.

### Known limits

- Cross-monitor dragging and toolbar recovery passed automated tests. Unplugging and reconnecting a real monitor still need a hands-on check.
- Intel tests run under Rosetta on an Apple Silicon Mac. No Intel Mac has run this version yet.
- Installing on another Mac and updating from an earlier version still need a hands-on check.
- VoiceOver and Full Keyboard Access checks remain incomplete.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.

## 0.1.0-alpha.9 (build 17) - 2026-09-30

### Added

- Copy, cut and paste now work for any object: shapes, text, arrows, drawings and images. Select it and press Command-C or Command-X, then Command-V. Copy, Cut and Paste are also in the right-click menu.
- When you draw an arrow or line near a shape, the shape is highlighted and a dot shows the point where the arrow will attach. The highlight also shows when you drag the end of an existing arrow.

### Changed

- Paste puts the copy under the pointer. If the pointer is on the original, each paste moves a little down and to the right.

### Fixed

- Text inside a shape could not be selected or edited when the shape was drawn after the text. Now a click selects the text and a double-click edits it, so you can change its font size. Double-click an empty part of the shape to edit the shape's own label.
- An arrow end that you drag onto a shape now attaches to it. Before, the end stayed loose.

### Known limits

- Intel support is new. Its automated tests passed under Rosetta on an Apple Silicon Mac, but no Intel Mac has run it yet.
- Boards with gradients or matching labels need 0.1.0-alpha.5 or later. Older boards still open.
- Installing from the disk image, the Move to Applications offer and signed app updates still need a check on another Mac.
- VoiceOver support is incomplete.
- The new toolbar editor and welcome tour still need a VoiceOver and Full Keyboard Access check.
- Copy and paste, text inside shapes and the arrow highlight passed automated tests but have not been tried by hand yet.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Physical multi-monitor retesting and older-board compatibility remain open.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.
- Native usage delivery and the 45-day metrics cleanup still need live checks.

## 0.1.0-alpha.8 (build 16) - 2026-09-30

### Added

- Settings has a new Toolbar & Shortcuts tab. Drag tools to arrange your toolbar, drag more in from More tools, or drag one out to remove it.
- To change a key, click the key under a tool in Settings and press the new one. Every tool can have a key now, including diagram shapes, and you can use Shift with a letter when the plain letters are taken.
- Spotlight, Cursor halo, Pointer lens, Smart draw, Blur, Pixelate and Distort can be toolbar buttons now. Drag them in from More tools. Properties, Board and Clear all are always in the toolbar, and every one of these can have its own key.
- A Minimal toolbar uses smaller buttons without key labels. Choose Minimal or Expanded in Settings > Toolbar & Shortcuts.
- Share style between tools, in Settings > General, applies a color, fill, width or line style change to every drawing tool. It stays off until you turn it on.
- The welcome tour now shows each feature with a short animation you can replay, and you can move between pages with the arrow keys. With Reduce Motion on, the pictures stay still.

### Changed

- If you pick a key that another tool uses, the key moves to the new tool and Settings names the tool that lost it. A button such as Give 2 back to Rectangle puts it back.
- The on-screen tips now say how to turn them off. The setting is now called Show tips on screen.

### Fixed

- On macOS 26.7, the Settings tabs could fold away behind a » button. They now always show at the top of the window.
- You can now resize the Settings window by dragging its edges. It can't get smaller than its content.

### Known limits

- Intel support is new. Its automated tests passed under Rosetta on an Apple Silicon Mac, but no Intel Mac has run it yet.
- Boards with gradients or matching labels need 0.1.0-alpha.5 or later. Older boards still open.
- Installing from the disk image, the Move to Applications offer and signed app updates still need a check on another Mac.
- VoiceOver support is incomplete.
- The new toolbar editor and welcome tour still need a VoiceOver and Full Keyboard Access check.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Physical multi-monitor retesting and older-board compatibility remain open.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.
- Native usage delivery and the 45-day metrics cleanup still need live checks.

## 0.1.0-alpha.7 (build 15) - 2026-09-27

### Added

- Annotator now runs on Intel Macs with macOS 14 or later, as well as on Apple Silicon. One app works on both. No Intel Mac has tried it yet, so please report anything that doesn't work.

### Changed

- Installing with Homebrew now takes one command: brew install --cask dorlugasigal/annotator/annotator. Homebrew adds the Annotator tap by itself.
- The AI skill now guides your assistant through making annotated screenshots and short demo clips. It plans the steps, sets up the screen, uses the macOS Screenshot tool or asks you to record, and checks the result before giving you the file.
- Annotator's website moved to getannotator.app. The app now opens its Help and download pages there and checks there for updates.

### Known limits

- Intel support is new. Its automated tests passed under Rosetta on an Apple Silicon Mac, but no Intel Mac has run it yet.
- Boards with gradients or matching labels need 0.1.0-alpha.5 or later. Older boards still open.
- Installing from the disk image, the Move to Applications offer and signed app updates still need a check on another Mac.
- VoiceOver support is incomplete.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Physical multi-monitor retesting and older-board compatibility remain open.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.
- Native usage delivery and the 45-day metrics cleanup still need live checks.

## 0.1.0-alpha.6 (build 14) - 2026-09-27

### Added

- Annotator now also comes as a disk image. Open it and drag Annotator into Applications; the window shows where to drop it.
- If you open Annotator from outside the Applications folder, it offers to show it in Finder so you can drag it there. Annotator can update itself only from Applications. It never moves itself or changes macOS security checks.

### Changed

- Short website demos fit their content more closely instead of showing the empty capture area below the toolbar.
- The pointer and shortcut demos on the website now zoom in on the settings being changed, so the text is easier to read.
- The website shows real tool-library navigation, settings changes and labels typed as you watch, with full preferences visible and a steady tool row.
- Get Annotator leads to a clear choice of download links or a copyable Homebrew command.
- The selected demonstration repeats while visible, with a compact pause control. Reduced-motion and script-free visits remain supported.
- The website also has clearer feature descriptions and link previews.
- The website now opens with a 30-second reel of the app, set to music. It starts with a tour of the toolbar and shows each tool in use. It starts muted; press Sound to hear it. It plays while you can see it and has a Pause button that is always shown. With reduced motion, it waits for you to press Play.
- The tool examples on the website have their own section below the reel, with new, sharper clips of Circle, Arrow, Highlight, Smart Draw, Shapes, Spotlight, Zoom and Blur. Choosing an example no longer moves the page. A list below the clips names more tools and shows how to open the tool library.
- Website sections no longer fade in as you scroll. Buttons press in when you click them, and the highlight slides to the example you choose. Reduced-motion and script-free visits see the finished page at once.
- The install section shows the Mac requirements next to the download button and draws the three install steps on a small Mac desktop. The Homebrew commands appear one per line, so they are easier to read and copy.

### Fixed

- Website videos keep their preview while loading, offer Play when autoplay is blocked, and can retry a failed download. Icons stay sharp on phones.
- A cancelled video start no longer stops newer playback. Video errors explain whether the download failed or the browser could not read the file.
- Website clips use a lower-load video format at the same resolution and speed. Changed recordings get fresh download URLs instead of reusing cached video files.
- Website videos show one compact Play and Pause control instead of the browser's controls. If a video fails, the browser's controls come back with an error and a direct link.
- Local website previews return the correct video bytes when a browser asks for the end of a file.
- Website videos use the documented request mode for service-worker previews.
- Website videos play on iPhone again. Caption tracks stopped iPhone browsers from loading any video that had one, so the silent demos now describe themselves in the page text instead.
- In Chrome, a website tool clip that fails to download now shows its error and a direct link instead of loading forever.

### Known limits

- Boards with gradients or matching labels need 0.1.0-alpha.5 or later. Older boards still open.
- Installing from the new disk image, the Move to Applications offer and signed app updates still need a check on another Mac.
- VoiceOver support is incomplete.
- With two displays side by side, dropping the toolbar on the edge where they meet docks it on the other display.
- Physical multi-monitor retesting and older-board compatibility remain open.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.
- Native usage delivery, scheduled metrics aggregation and cleanup still need live checks.

## 0.1.0-alpha.5 (build 13) - 2026-09-24

### Added

- Expand Color wheel inside the palette to choose a custom color, or pick from eight diagonal gradient swatches.
- A short, optional introduction walks through drawing, colors, focus tools and permission setup.
- Choose your cursor highlight's shape, outline, size, color, opacity and glow. Try Pulse, Ripple or Turn by clicking the preview.
- Pick the pointer's custom color inside Settings, without opening another window.

### Changed

- Stroke and fill have separate opacity controls. New strokes start at 100%; a new fill starts at 40%. Shapes still start with no fill.
- Turn on Fill shape to start with the outline's color or gradient. Match outline copies it again without changing your fill opacity.
- New shape labels match the outline's color or gradient. Choose another text color, or use Match outline to link it again.
- The website groups its real app recordings into a feature tour, with examples for demos, reviews and teaching.

### Fixed

- Website updates keep the owner dashboard and usage-count service configured.
- Changing a color keeps the opacity you chose for that stroke or fill.
- Adding a fill keeps the color palette open while you choose its color.
- Color palettes fit their contents instead of scrolling when there is enough space.
- Cursor highlights no longer pick up a dark window shadow around their glow.

### Known limits

- Boards with gradients or matching labels need this version or later. Older boards still open.
- Installation on another Mac and signed app updates still need verification.
- VoiceOver support is incomplete.
- Physical multi-monitor retesting and older-board compatibility remain open.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Heavy gradient scenes and long-session resource use still need native performance checks.
- Native usage delivery, scheduled metrics aggregation and cleanup still need live checks.

## 0.1.0-alpha.4 (build 12) - 2026-09-24

### Added

- Choose whether to share broad daily usage counts. Sharing is off by default and never includes drawings, screenshots, typed text or file names.

### Changed

- The introduction puts three shortcuts first, with readable keycaps and a shorter welcome.
- Settings has more room for tool shortcuts, clearer permission states and a centered About view. Help keeps extra detail out of the way.
- Coffee support stands out in Settings and the menu. Not now still snoozes reminders; the reminder off switch is no longer shown.
- Permission, update and feedback messages explain the next step. Feedback shows when text goes to GitHub and how to copy a report that is too long for a browser link.
- The website has a new dark palette, simpler framing and fresh recordings with the real dark toolbar: a rough teal ellipse, a blue curved arrow edited by its points, and blur over a colorful image.
- The website help, privacy page and installation guides use clearer names, current download links and readable Mac requirements.
- The main download button opens GitHub releases. Homebrew install steps are available on the Downloads page.

### Fixed

- Help no longer suggests that covers follow moving windows. Restart advice warns that quitting removes covers.
- AI setup explains the separate app's permissions and privacy limits. Saving guidance warns that editable boards keep original pasted images.
- Starting a drawing closes the color picker without an extra Done click.

### Known limits

- Usage collection still needs a live service check.
- Installation on another Mac and signed app updates still need verification.
- VoiceOver support is incomplete.
- Physical multi-monitor retesting and older-board compatibility remain open.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Whole-app performance targets and long-session resource use are not yet verified.

## 0.1.0-alpha.3 (build 11) - 2026-09-23

### Added

- A first-use introduction explains drawing, using other apps and your configured shortcuts. Reopen it in Settings or choose Don't show this again.
- A new app icon and clearer Check for updates and Buy me a coffee actions in Settings and the menu.

### Changed

- Help and About are now part of Settings.
- The website and public README have clearer downloads, feature descriptions and optional coffee support.
- New Homebrew publishing uses the install name annotator, including for alpha releases.

### Fixed

- Starting a text annotation and leaving it empty no longer saves an empty shape or adds an undo step.

### Known limits

- Installation on another Mac and signed app updates still need verification.
- VoiceOver support is incomplete.
- Physical multi-monitor retesting and older-board compatibility remain open.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Whole-app performance targets and long-session resource use are not yet verified.

## 0.1.0-alpha.2 (build 10) - 2026-09-23

### Added

- Open What's changed in Settings or Help to read the notes for your installed version.
- Use Report a problem or Share feedback in Help. Copy your report or review a draft on GitHub before sending it.
- Optional support reminders appear after continued use. Choose Not now to wait at least 30 days, or turn reminders off in Help.

### Changed

- Help links now open the public Annotator issue tracker.
- The website is available with a field guide, privacy information and optional support. All tools remain free.

### Known limits

- Installation on another Mac and signed app updates still need verification.
- VoiceOver support is incomplete.
- Physical multi-monitor retesting and older-board compatibility remain open.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Whole-app performance targets and long-session resource use are not yet verified.

## 0.1.0-alpha.1 (build 9) - 2026-09-23

### Added

- The first numbered private alpha for user testing.

### Changed

- App and package labels now show the alpha stage separately from the numeric Mac app version.

### Known limits

- This development-signed build is not notarized. Installation on another Mac is not yet verified.
- VoiceOver needs further work and is deferred for this private alpha.
- Physical multi-monitor retesting and older-board compatibility remain open.
- Effects do not follow moving windows or scrolling content and are not secure redaction.
- Whole-app performance targets and long-session resource use are not yet verified.
