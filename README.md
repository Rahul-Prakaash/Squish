# Squish

Squish is a small Windows app that shrinks files and folders into archives, and then proves that nothing was lost.

You drag things in, it tells you roughly how small they will get, you press Start. When it finishes, every archive has been checked against your original files. It works completely offline.

Squish does the clicking for you. The actual compression is done by [7-Zip](https://www.7-zip.org/), a well known free compression program.

## What it does

- **Drag and drop.** Drop files or folders on the window. Each one becomes its own archive, or you can pack them all into one.
- **Predicts the size first.** Before you start, Squish compresses a small sample of your files and estimates the final size.
- **Proves it is lossless.** After compressing, Squish unpacks the archive in memory and compares it with your originals. If anything differs, the archive is thrown away and you are told.
- **Never touches your originals.** Squish only ever creates archives. It does not change or delete the files you gave it.
- **Works offline.** No account, no internet connection, nothing sent anywhere.
- **Light and dark themes.** Switch with the sun and moon button.
- **Advanced settings for people who want them.** Format, method, dictionary size, password, split volumes and more.

## Requirements

- Windows 10 or Windows 11.
- 7-Zip installed, or a copy of 7-Zip sitting next to the app (the portable build below includes one).

Nothing else needs to be installed. The tools needed to build the app already come with Windows.

## Getting started

### 1. Get the code

Download this repository as a ZIP (the green **Code** button, then **Download ZIP**) and unpack it, or clone it:

```
git clone https://github.com/Rahul-Prakaash/Squish.git
```

### 2. Build it

Double-click `build.bat`.

That is the whole build. It uses the C# compiler that is already part of Windows, so there is no Visual Studio or SDK to install. After a second or two you will have:

- `Squish.exe` - the app.
- `Portable\Squish\` - a folder containing the app plus a copy of 7-Zip's engine. Copy this folder to a USB stick or another PC and it will run there, even if 7-Zip is not installed. This part is only made if 7-Zip is installed on the PC doing the build.

### 3. Run it

Double-click `Squish.exe`.

## How to use it

1. Drag files or folders onto the window. You can also use **Add Folder** and **Add Files**.
2. Look at the predicted size shown on each item.
3. Pick a compression level. **Normal** is quick. **Ultra** makes the smallest archive but takes the longest.
4. Press **Start**.

When an item finishes it shows **Verified lossless**. Each finished item has two buttons: one opens the folder containing the archive, the other deletes the archive (it goes to the Recycle Bin, and your original files are left alone).

By default each archive is saved next to the original. Use **Save to** if you would rather choose one folder for all of them.

## How the lossless check works

"Lossless" means that when you unpack the archive, you get back exactly the same files, byte for byte.

Squish does not just assume that. For every archive it:

1. Compresses your files.
2. Unpacks the finished archive in memory and calculates a SHA-256 fingerprint of every file inside.
3. Calculates the same fingerprints for your original files.
4. Compares the two. File contents, file names, the number of files and folders, and the total size must all match.

A SHA-256 fingerprint changes completely if even one byte of a file is different, so a match means the archive really is a perfect copy.

If the check fails, or if some files could not be read (for example because another program had them open), Squish deletes the archive and marks the item as failed. This check always runs and cannot be switched off.

## How accurate is the size prediction?

Squish takes 32 small pieces spread evenly through your files, compresses them with the same settings you chose, and scales the result up.

In testing on real files it was usually within about 5% of the final size. Folders full of many small, similar files tend to end up smaller than predicted.

How much space you save depends on what you compress:

- Text, documents, source code and programs shrink a lot.
- Videos, music, photos and existing zip files are already compressed, so they barely shrink.

## Advanced settings

The **Advanced** button opens the same options as 7-Zip's own "Add to Archive" window:

| Option | Choices |
| --- | --- |
| Archive format | 7z, zip, tar |
| Compression level | Store, Fastest, Fast, Normal, Maximum, Ultra |
| Compression method | LZMA2, LZMA, PPMd, BZip2 for 7z. Deflate, Deflate64, BZip2, LZMA, PPMd for zip |
| Dictionary size, word size, solid block size | Depends on the method |
| CPU threads and memory limit | How much of the PC 7-Zip may use |
| Split into volumes | Cuts the archive into pieces of a fixed size |
| Password | AES-256 encryption, with the option to hide file names |
| Self-extracting archive | Makes a `.exe` that unpacks itself |
| Parameters | Extra 7-Zip switches typed by hand |

The lossless check runs for every one of these combinations. Passwords are never saved to disk.

One switch is refused on purpose: `-sdel`, which tells 7-Zip to delete your files after compressing them. Squish never touches your originals.

If you are not sure what an option does, leave it on **Auto**. **Use simple settings** in that window takes you back to the two simple choices.

## Project layout

| File | What it is |
| --- | --- |
| `Squish.cs` | The whole app, in one C# file |
| `build.bat` | Builds the app and the portable folder |
| `app.ico` | The app icon |
| `make-icon.ps1` | The script that draws `app.ico` |
| `fonts\` | The Jost typeface, which is built into the app |
| `README.txt` | The short guide that ships inside the portable folder |

The app is written in C# with Windows Forms and targets the .NET Framework that is built into Windows. It has no other dependencies.

## Credits

- [7-Zip](https://www.7-zip.org/) by Igor Pavlov does the compression. It is free software, mostly under the GNU LGPL.
- The typeface is [Jost](https://indestructibletype.com/Jost.html) by indestructible type*, used under the SIL Open Font License. The license is in `fonts\OFL.txt`.
