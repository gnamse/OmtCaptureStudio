# Blank broadcast: how to capture the evidence we need

**The problem:** you broadcast your screen, the other PC sees the source in its list, you
select it - and the picture stays black.

**What we need from you:** one log file from the **broadcasting PC**. It will now tell us
whether that PC is failing to capture the screen at all, or whether the video is leaving it
and being lost on the way. Those are completely different problems, and the log
distinguishes them.

---

## Step 1 - update the app first (about 2 minutes)

The logging we need was added in **v1.0.0.23**. If your broadcasting PC is on an older
version, the app will offer it to you:

1. Open **OMT Capture Studio** and leave it open for a minute.
2. Within about 30 seconds a small banner appears in the status bar:
   **`v1.0.0.23 available`** with an **Install** button.
3. Click **Install**. The app downloads it, checks it, closes, installs itself and reopens.

> **Check first:** the version number is shown in the bottom-right of the app window. If it
> already says `v1.0.0.23`, skip straight to Step 2.
>
> You can also install manually from
> https://github.com/gnamse/OmtCaptureStudio/releases/latest - download the **Setup** file
> and run it.

---

## Step 2 - reproduce the problem, then send us the log

1. On the **broadcasting PC**, open **OMT Capture Studio**.
2. Go to **Broadcast** mode and start broadcasting **Screen 1** - your normal setup.
3. Now do what makes the picture go blank on the other PC: connect from the second PC and
   watch it stay black.
4. **Leave it running for at least 30 seconds**, exactly as it is. Do not close the app yet.
5. Now close the app normally.

Then find the log file:

- Press **Windows key + R**, paste the line below into the box, and press Enter:

```
%LOCALAPPDATA%\Programs\OMT Capture Studio\logs
```

- If that folder does not exist or is empty, try the other install location:

```
C:\Program Files\OMT Capture Studio\logs
```

6. Open the newest file in that folder - it is named after today's date, for example
   `2026-10-29.log`.
7. **Send us that whole file.** Copying its contents into an email is fine.

---

## Step 3 - three things to tell us alongside it

1. **The version number** from the bottom-right of the app window (e.g. `v1.0.0.23`).
2. **How many monitors** the PC has, and which one you picked as "Screen 1".
3. **The screen resolution** - Windows **Settings -> System -> Display**, look for
   "Display resolution".

---

## What the log will tell us (you can check it yourself)

Open the log and search for this sentence:

```
Broadcast is running but no frames are being captured (screen capture unavailable).
```

**If you find it** - that PC is capturing nothing at all, which fully explains the blank
picture. The same line also says *why*; it may end like this:

```
... has not produced a single frame in 10 s (DXGI Desktop Duplication unavailable, using GDI fallback).
```

That means Windows would not give the app its fast screen-capture path and it fell back to
the slow one - a graphics-driver or desktop-session issue on that PC. Send us that line and
we will know where to look.

**If you do NOT find it** during a blank broadcast - that is useful news too. It means the
PC *is* capturing and sending the picture, and the video is being lost between the two
machines or at the receiving end. Tell us that as well; it rules out half the possibilities.

---

## If the log folder is missing entirely

Tell us and we will take it from there - it may mean the app is installed somewhere
unusual, and the version number from Step 3 will help us work out where to look.

---

## Advanced option - only if we send you an extra file

If we send you the test executable that ships with a development build, it can measure
screen capture directly in about 15 seconds, without broadcasting at all. On the
broadcasting PC, open **Command Prompt** and run:

```
"%LOCALAPPDATA%\Programs\OMT Capture Studio\OmtCaptureStudio.Tests.exe" --diag-capture 8
```

- `captured=` **greater than 0** - capture is working; the problem is elsewhere.
- `captured=0` - capture is broken, and that explains everything.

Send us the whole output. (The `8` is just how many seconds to measure; anything from 5
to 30 works.)
