// A small CSV writer for files made in the browser (e.g. the rows of an
// import that need fixing). Same rules as the server's CsvExport: cells that
// a spreadsheet would run as a formula get a leading apostrophe; UTF-8 with
// a byte-order mark so Excel reads accented names correctly.
export class CsvWriter {
  private lines: string[] = [];

  row(cells: (string | null | undefined)[]): void {
    this.lines.push(cells.map((c) => CsvWriter.escape(c ?? '')).join(','));
  }

  blob(): Blob {
    return new Blob(['﻿' + this.lines.join('\r\n') + '\r\n'], { type: 'text/csv;charset=utf-8' });
  }

  static escape(value: string): string {
    let text = value;
    if (text.length > 0 && '=+-@\t\r'.includes(text[0])) text = "'" + text;
    return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
  }
}
