// Client-side PDF of a certificate exactly as the page shows it, including
// its QR code — the PDF is a picture of the verified certificate, and the
// QR code on it leads back to the live record. Loaded on demand so the
// verify page doesn't ship html2canvas/jsPDF to every visitor.
export async function downloadCertificatePdf(element: HTMLElement, fileName: string): Promise<void> {
  const [{ default: html2canvas }, { jsPDF }] = await Promise.all([import('html2canvas'), import('jspdf')]);

  // The preview may be CSS-scaled down to fit the screen; render the
  // element's natural size at 2x for a sharp print.
  const canvas = await html2canvas(element, {
    scale: 2,
    useCORS: true,
    backgroundColor: '#ffffff',
    logging: false,
  });

  const landscape = canvas.width >= canvas.height;
  const pdf = new jsPDF({ orientation: landscape ? 'landscape' : 'portrait', unit: 'mm', format: 'a4' });
  const pageWidth = pdf.internal.pageSize.getWidth();
  const pageHeight = pdf.internal.pageSize.getHeight();
  const margin = 10;
  const ratio = Math.min((pageWidth - 2 * margin) / canvas.width, (pageHeight - 2 * margin) / canvas.height);
  const width = canvas.width * ratio;
  const height = canvas.height * ratio;

  pdf.addImage(canvas.toDataURL('image/png'), 'PNG', (pageWidth - width) / 2, (pageHeight - height) / 2, width, height);
  pdf.save(fileName);
}
