#!/usr/bin/env bash
# Genera le immagini di prova usate dal banco di test.
# Richiede: ffmpeg, genisoimage (pacchetto genisoimage / cdrkit).
set -euo pipefail

cd "$(dirname "$0")"
rm -rf work && mkdir -p work/disc/VIDEO_TS work/vr/DVD_RTAV
cd work

echo "1/4 genero due clip MPEG-2 in formato DVD..."
ffmpeg -v error -f lavfi -i "testsrc2=size=720x576:rate=25:duration=6" \
       -f lavfi -i "sine=frequency=440:duration=6" -target pal-dvd -y clip1.mpg
ffmpeg -v error -f lavfi -i "smptebars=size=720x576:rate=25:duration=4" \
       -f lavfi -i "sine=frequency=880:duration=4" -target pal-dvd -y clip2.mpg

echo "2/4 costruisco un disco grezzo senza filesystem (caso peggiore: nessun UDF, nessun IFO)..."
python3 - <<'PY'
S = 2048
d1 = open('clip1.mpg', 'rb').read()
d2 = open('clip2.mpg', 'rb').read()
with open('../disc.bin', 'wb') as out:
    out.write(b'\x00' * (16 * S))     # area di sistema vuota
    out.write(d1)                      # prima registrazione
    out.write(b'\x00' * (10 * S))      # buco breve: non deve spezzare il titolo
    out.write(d1[:len(d1)//2])         # coda con orologio che riparte
    out.write(b'\x00' * (200 * S))     # buco lungo: separa le registrazioni
    out.write(d2)                      # seconda registrazione
print("disc.bin creato")
PY

echo "3/4 costruisco un DVD-Video con UDF (due VOB)..."
cp clip1.mpg disc/VIDEO_TS/VTS_01_1.VOB
cp clip2.mpg disc/VIDEO_TS/VTS_01_2.VOB
genisoimage -quiet -udf -V "DVDTEST" -o ../dvdvideo.iso disc/

echo "4/4 costruisco un DVD-VR da videocamera (un VRO con due registrazioni)..."
cat clip1.mpg clip2.mpg > vr/DVD_RTAV/VR_MOVIE.VRO
printf 'DVD_RTR_VMG0' > vr/DVD_RTAV/VR_MANGR.IFO
head -c 2036 /dev/zero >> vr/DVD_RTAV/VR_MANGR.IFO
genisoimage -quiet -udf -V "CAMCORDER" -o ../dvdvr.iso vr/

cd ..
rm -rf work
ls -l disc.bin dvdvideo.iso dvdvr.iso
echo
echo "Pronto. Ora:  dotnet run --project TestHarness.csproj -c Release"
