// Утилиты скачивания файлов, отданных Web API как blob (PDF-отчёты, S3-артефакты).

// Имя файла из заголовка Content-Disposition.
// Поддерживает filename*=UTF-8''… (RFC 5987, кириллица), filename="…" и bare filename=….
export function parseContentDispositionFilename(disposition: string | undefined): string | null {
  if (!disposition) return null
  const utf8 = /filename\*=UTF-8''([^;]+)/i.exec(disposition)
  if (utf8) return decodeURIComponent(utf8[1])
  const quoted = /filename="([^"]+)"/i.exec(disposition)
  if (quoted) return quoted[1]
  const bare = /filename=([^;]+)/i.exec(disposition)
  return bare ? bare[1].trim() : null
}

// Сохранить blob как файл: временный <a download> + клик.
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  document.body.appendChild(a)
  a.click()
  a.remove()
  URL.revokeObjectURL(url)
}
