import { useState, useRef } from 'react'
import styles from './App.module.css'

interface Citation {
  articleNumber: string
  paragraphNumber: string
  articleTitle: string
  excerpt: string
}

interface AskResponse {
  answer: string
  citations: Citation[]
  usedTool: boolean
  retrievalMode: string
}

type State =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'error'; message: string }
  | { status: 'done'; response: AskResponse }

export default function App() {
  const [question, setQuestion] = useState('')
  const [state, setState] = useState<State>({ status: 'idle' })
  const abortRef = useRef<AbortController | null>(null)

  async function submit() {
    const q = question.trim()
    if (!q) return

    abortRef.current?.abort()
    const controller = new AbortController()
    abortRef.current = controller

    setState({ status: 'loading' })

    try {
      const res = await fetch('/ask', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ question: q }),
        signal: controller.signal,
      })

      if (!res.ok) {
        const text = await res.text().catch(() => res.statusText)
        setState({ status: 'error', message: `API error ${res.status}: ${text}` })
        return
      }

      const data = (await res.json()) as AskResponse
      setState({ status: 'done', response: data })
    } catch (err) {
      if ((err as Error).name === 'AbortError') return
      setState({ status: 'error', message: (err as Error).message })
    }
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) {
      e.preventDefault()
      void submit()
    }
  }

  return (
    <div className={styles.layout}>
      <header className={styles.header}>
        <h1>UCL Regulations</h1>
        <p>Ask a question about the UEFA Champions League 2025/26 regulations.</p>
      </header>

      <main className={styles.main}>
        <div className={styles.inputGroup}>
          <textarea
            className={styles.textarea}
            value={question}
            onChange={e => setQuestion(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="e.g. How many locally trained players must a club register?"
            rows={3}
            aria-label="Question"
          />
          <button
            className={styles.button}
            onClick={() => void submit()}
            disabled={state.status === 'loading' || !question.trim()}
          >
            {state.status === 'loading' ? 'Asking…' : 'Ask'}
          </button>
          <p className={styles.hint}>Ctrl+Enter to submit</p>
        </div>

        {state.status === 'error' && (
          <div className={styles.error} role="alert">
            {state.message}
          </div>
        )}

        {state.status === 'done' && (
          <section className={styles.result}>
            <div className={styles.meta}>
              <span className={styles.modeBadge}>{state.response.retrievalMode}</span>
              {state.response.usedTool && (
                <span className={styles.toolBadge}>roster tool used</span>
              )}
            </div>

            <div className={styles.answer}>
              <p>{state.response.answer}</p>
            </div>

            {state.response.citations.length > 0 && (
              <div className={styles.citations}>
                <h2>Citations</h2>
                <ol>
                  {state.response.citations.map(c => (
                    <li key={`${c.articleNumber}.${c.paragraphNumber}`} className={styles.citation}>
                      <strong>
                        {c.articleNumber}.{c.paragraphNumber} — {c.articleTitle}
                      </strong>
                      <blockquote className={styles.excerpt}>{c.excerpt}</blockquote>
                    </li>
                  ))}
                </ol>
              </div>
            )}

            {state.response.citations.length === 0 && (
              <p className={styles.noCitations}>
                No citations — the regulations do not cover this question.
              </p>
            )}
          </section>
        )}
      </main>
    </div>
  )
}
