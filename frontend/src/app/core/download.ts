// Saves a downloaded file (e.g. a CSV fetched with the bearer token).
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.click();
  URL.revokeObjectURL(url);
}

// Error bodies of blob requests arrive as a Blob; read the API's message.
export async function blobErrorMessage(err: { error?: unknown }, fallback: string): Promise<string> {
  try {
    return JSON.parse(await (err.error as Blob).text()).error ?? fallback;
  } catch {
    return fallback;
  }
}
