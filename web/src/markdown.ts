import { marked } from 'marked'
import DOMPurify from 'dompurify'

marked.setOptions({ gfm: true, breaks: true })

/** Markdown を HTML にする（スクリプトなどは取り除く）。リンクは別のタブで開く。 */
export function render(md: string): string {
  const html = marked.parse(md, { async: false }) as string
  const clean = DOMPurify.sanitize(html)
  return clean.replace(/<a /g, '<a target="_blank" rel="noopener noreferrer" ')
}
