# Checking whether screen capture is working (OMT Capture Studio)

**What this answers:** when you broadcast your screen and the other PC shows a blank
picture, there are two possible causes — the video never left your PC, or it left but
something else dropped it. This one command tells you which, in about 15 seconds.

---

## How to run it

1. On the **broadcasting PC** (the one whose screen is blank on the other end), open a
   **Command Prompt** or **PowerShell** window.
2. Copy and paste this line, then press Enter — **change the path if the app is installed
   somewhere else** (the usual location is shown):

```
"%LOCALAPPDATA%\Programs\OMT Capture Studio\OmtCaptureStudio.Tests.exe" --diag-capture 8
```

If you get *"is not recognized"* or *"cannot find the path"*, the app is installed
somewhere else. Find the folder that contains `OmtCaptureStudio.exe`, and use that folder
for the line above instead, for example:

```
"C:\Program Files\OMT Capture Studio\OmtCaptureStudio.Tests.exe" --diag-capture 8
```

> The number `8` at the end is just how many seconds to measure. Any number from 5 to 30
> is fine.

3. **Wait about 15 seconds.** The window stays still for a moment, then prints a result.
4. Copy everything it printed and send it back.

---

## What the output means

### If it looks like this — capture is FINE:

```
RESULT: TryCaptureFrame -> captured=305  noNewFrame=0 over 8s
   => the capture provider DELIVERS frames; a blank broadcast is NOT a capture failure.
```

Your PC **is** capturing the screen (305 pictures in 8 seconds in this example — the exact
number will differ). The problem is somewhere else, and the numbers in the first line
(`1920x1080 stride=7680`) tell us the resolution being captured.

### If it looks like this — capture is BROKEN:

```
RESULT: TryCaptureFrame -> captured=0  noNewFrame=<something> over 8s
   => the capture provider delivered ZERO frames ...
```

Your PC is capturing **nothing at all**. That fully explains the blank picture: the
broadcast is advertised on the network, but no video (and no audio) is ever sent.

### If it shows an error

Copy the whole error message — it names what Windows refused to give us (graphics driver,
screen-lock, a full-screen game, etc.).

---

## Please also send these two things

1. **The first line**, which shows the resolution:
   `geometry : 1920x1080  stride=7680  buffer=pinned OK`
2. **How many monitors you have**, and which one you selected as "Screen 1".

---

## If the file is missing

If `OmtCaptureStudio.Tests.exe` isn't in the folder at all, that's fine — just tell us, and
we'll get you a build that includes it. (It ships alongside the app for exactly this kind
of check.)
