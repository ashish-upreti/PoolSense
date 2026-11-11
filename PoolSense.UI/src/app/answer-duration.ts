export function formatAnswerDuration(milliseconds?: number | null): string | null {
  if (typeof milliseconds !== 'number' || !Number.isFinite(milliseconds) || milliseconds < 0) return null
  if (milliseconds < 100) return '<0.1 s'
  if (milliseconds < 60_000) return `${(milliseconds / 1000).toFixed(1)} s`
  const totalSeconds = Math.round(milliseconds / 1000)
  const minutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  return `${minutes} min ${seconds} s`
}
