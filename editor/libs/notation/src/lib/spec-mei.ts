/**
 * MEI for the renderer's tests and ADR-0011's spike (not exported): `staves` staves (treble and bass alternating) of
 * `bars` 4/4 bars, each bar four beamed eighths, two quarters (the last with a syllable), a dynamic and a chord symbol
 * on the first staff, with ids as the engine writes them (`n{noteId}`, `l…`, `h…`, `s…`). `moved` raises one note
 * (the one-note edit).
 */
export function spikeMei(bars: number, staves: number, moved: number | null = null, version = '5.1'): string {
  const steps = ['c', 'd', 'e', 'f', 'g', 'a', 'b'];
  let id = 1;
  const parts: string[] = [
    `<?xml version="1.0" encoding="UTF-8"?>`,
    `<mei xmlns="http://www.music-encoding.org/ns/mei" meiversion="${version}">`,
    `<meiHead><fileDesc><titleStmt><title>Spike</title></titleStmt><pubStmt/></fileDesc></meiHead>`,
    `<music><body><mdiv><score><scoreDef meter.count="4" meter.unit="4" key.sig="0">`,
    `<pgHead><rend halign="center" valign="top"><rend fontsize="x-large">Spike Title</rend></rend>`,
    `<rend halign="right" valign="bottom">Ann Composer</rend></pgHead><staffGrp>`
  ];
  for (let staff = 1; staff <= staves; staff++) {
    const treble = staff % 2 === 1;
    parts.push(
      `<staffDef n="${staff}" lines="5" clef.shape="${treble ? 'G' : 'F'}" clef.line="${treble ? 2 : 4}">`,
      `<label>Part ${staff}</label></staffDef>`
    );
  }
  parts.push('</staffGrp></scoreDef><section>');
  for (let bar = 1; bar <= bars; bar++) {
    parts.push(`<measure n="${bar}" xml:id="m${bar}">`);
    for (let staff = 1; staff <= staves; staff++) {
      const octave = staff % 2 === 1 ? 4 : 3;
      parts.push(`<staff n="${staff}"><layer n="1"><beam>`);
      for (let k = 0; k < 4; k++) {
        const step = (bar * 3 + k + staff + (id === moved ? 1 : 0)) % 7;
        parts.push(`<note xml:id="n${id++}" dur="8" pname="${steps[step]}" oct="${octave}"/>`);
      }
      parts.push(`</beam><note xml:id="n${id++}" dur="4" pname="c" oct="${octave + 1}"/>`);
      const sung = id++;
      parts.push(
        `<note xml:id="n${sung}" dur="4" pname="e" oct="${octave}">`,
        `<verse n="1"><syl xml:id="llyr_${sung}">la</syl></verse></note></layer></staff>`
      );
    }
    parts.push(
      `<dynam xml:id="ssym_${bar}" staff="1" tstamp="1">mf</dynam>`,
      `<harm xml:id="hchd_${bar}" staff="1" tstamp="1">Am7</harm></measure>`
    );
  }
  parts.push('</section></score></mdiv></body></music></mei>');
  return parts.join('');
}
