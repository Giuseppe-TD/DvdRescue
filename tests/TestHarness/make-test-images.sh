#!/usr/bin/env bash
# Genera il materiale di prova: due DVD-Video veri (con dvdauthor) e un disco grezzo
# senza filesystem. Richiede ffmpeg, dvdauthor, genisoimage.
set -euo pipefail

cd "$(dirname "$0")"
export VIDEO_FORMAT=PAL     # dvdauthor lo pretende per la tabella dei contenuti
MEDIA="media"
rm -rf "$MEDIA" && mkdir -p "$MEDIA"
cd "$MEDIA"

echo "1/5 genero le clip MPEG-2 in formato DVD..."
ffmpeg -v error -f lavfi -i "testsrc2=size=720x576:rate=25:duration=20" \
       -f lavfi -i "sine=frequency=440:duration=20" -target pal-dvd -y t1.mpg
ffmpeg -v error -f lavfi -i "smptebars=size=720x576:rate=25:duration=12" \
       -f lavfi -i "sine=frequency=660:duration=12" -target pal-dvd -y t2.mpg
ffmpeg -v error -f lavfi -i "testsrc2=size=720x576:rate=25:duration=7" \
       -f lavfi -i "sine=frequency=880:duration=7" -target pal-dvd -y t3.mpg

echo "2/5 autoro un DVD-Video con 3 titoli, il primo diviso in 4 capitoli..."
cat > dvd.xml <<'XML'
<dvdauthor dest="dvd">
  <vmgm />
  <titleset>
    <titles>
      <pgc><vob file="t1.mpg" chapters="0,5,10,15" /></pgc>
      <pgc><vob file="t2.mpg" /></pgc>
      <pgc><vob file="t3.mpg" /></pgc>
    </titles>
  </titleset>
</dvdauthor>
XML
rm -rf dvd && dvdauthor -x dvd.xml >/dev/null 2>&1
genisoimage -quiet -dvd-video -o dvd.iso dvd/ 2>/dev/null

echo "3/5 autoro un secondo DVD con un titolo composto da più celle..."
for i in 1 2 3 4; do
  ffmpeg -v error -f lavfi -i "testsrc2=size=720x576:rate=25:duration=8" \
         -f lavfi -i "sine=frequency=$((300 + i * 100)):duration=8" -target pal-dvd -y "c$i.mpg"
done
cat > dvd2.xml <<'XML'
<dvdauthor dest="dvd2">
  <vmgm />
  <titleset>
    <titles>
      <pgc>
        <vob file="c1.mpg" /><vob file="c2.mpg" /><vob file="c3.mpg" /><vob file="c4.mpg" />
      </pgc>
    </titles>
  </titleset>
</dvdauthor>
XML
rm -rf dvd2 && dvdauthor -x dvd2.xml >/dev/null 2>&1
genisoimage -quiet -dvd-video -o dvd2.iso dvd2/ 2>/dev/null

echo "4/5 costruisco un disco grezzo senza filesystem (due registrazioni separate)..."
python3 - <<'PY'
S = 2048
a = open('t1.mpg', 'rb').read()
b = open('t2.mpg', 'rb').read()
with open('raw.bin', 'wb') as out:
    out.write(b'\x00' * (16 * S))       # area di sistema vuota
    out.write(a)                         # prima registrazione
    out.write(b'\x00' * (2000 * S))      # buco lungo: separa le registrazioni
    out.write(b)                         # seconda registrazione
print("raw.bin creato")
PY

echo "5/5 pulizia..."
rm -rf dvd dvd2
ls -lh dvd.iso dvd2.iso raw.bin

echo
echo "Pronto. Ora:  dotnet run --project ../TestHarness.csproj -c Release"
