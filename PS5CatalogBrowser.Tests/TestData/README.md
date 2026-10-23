# RAR reader regression fixture

`rar5-reader-regression.rar` contains only synthetic data, generated with WinRAR 6.22:

- `first.txt`: ASCII `Synthetic archive reader regression fixture.` followed by LF (45 bytes).
- `second.bin`: 64 copies of a 262144-byte block whose byte at index i is `(i * 37 + i / 17 + 11) % 256` (integer division); total 16 MiB.

Command: `rar a -ma5 -s -m5 regression.rar first.txt second.bin`.

SharpCompress 1.0.0's asynchronous RAR entry reads with a 1 MiB buffer produce
21,037,056 bytes from the second entry and then fail its CRC. Synchronous reads
produce the declared 16,777,216 bytes with the expected SHA-256 and pass CRC.
The test extracts through the production reader and verifies length and SHA-256.
No game files, passwords or third-party archives are included.

## Encrypted input-buffer regression

`rar5-encrypted-buffer-regression.rar` contains three synthetic files, generated
with WinRAR 6.22 using the test-only password `SyntheticArchivePassword`:

- `first.txt`: ASCII `Synthetic encrypted RAR buffer regression.` followed by LF (43 bytes).
- `second.bin`: 84880 bytes, byte i is `(i * 37 + i / 17 + 11) % 256` (integer division).
- `third.bin`: eight copies of a 98304-byte block filled by .NET `new Random(417).NextBytes(block)`.

Command: `rar a -ma5 -s- -m3 -md32m -hpSyntheticArchivePassword fixture.rar first.txt second.bin third.bin`.

With the default 65536-byte SharpCompress input buffer, synchronous decoding of
the third entry fails with `unpacked file size does not match header: expected
786432 found 0`. Setting `ReaderOptions.BufferSize = 0` produces the correct bytes.
The production regression tries an incorrect password before the correct one and
checks every extracted file's length and SHA-256. The password is synthetic;
no user credentials or game data are included.
