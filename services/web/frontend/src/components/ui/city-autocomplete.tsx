import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Search } from 'lucide-react'
import { citiesApi, type CitySuggestion } from '@/api/cities'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'

interface CityAutocompleteProps {
  onSelect: (city: CitySuggestion) => void
  excluded?: ReadonlyArray<string>
  placeholder?: string
}

export function CityAutocomplete({ onSelect, excluded = [], placeholder }: CityAutocompleteProps) {
  const [input, setInput] = useState('')
  const [debounced, setDebounced] = useState('')
  const [open, setOpen] = useState(false)
  const [highlighted, setHighlighted] = useState(0)
  const containerRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    const id = setTimeout(() => setDebounced(input.trim()), 200)
    return () => clearTimeout(id)
  }, [input])

  const query = useQuery({
    queryKey: ['cities', 'suggest', debounced],
    queryFn: ({ signal }) => citiesApi.suggest(debounced, 8, signal),
    enabled: open,
  })

  const excludedLower = useMemo(
    () => new Set(excluded.map((c) => c.toLowerCase())),
    [excluded],
  )

  const items = useMemo(
    () => (query.data?.items ?? []).filter((c) => !excludedLower.has(c.name.toLowerCase())),
    [query.data, excludedLower],
  )

  useEffect(() => {
    setHighlighted(0)
  }, [items])

  useEffect(() => {
    const onDocClick = (e: MouseEvent) => {
      if (!containerRef.current?.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', onDocClick)
    return () => document.removeEventListener('mousedown', onDocClick)
  }, [])

  const choose = (city: CitySuggestion) => {
    onSelect(city)
    setInput('')
    setOpen(false)
    inputRef.current?.focus()
  }

  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault()
      setOpen(true)
      if (items.length > 0) setHighlighted((h) => Math.min(h + 1, items.length - 1))
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      setHighlighted((h) => Math.max(h - 1, 0))
    } else if (e.key === 'Enter') {
      if (open && items.length > 0) {
        e.preventDefault()
        choose(items[highlighted])
      }
    } else if (e.key === 'Escape') {
      setOpen(false)
    }
  }

  return (
    <div ref={containerRef} className="relative">
      <Search
        size={16}
        className="absolute left-3 top-1/2 -translate-y-1/2 text-text-tertiary pointer-events-none"
      />
      <Input
        ref={inputRef}
        className="h-11 pl-10"
        placeholder={placeholder ?? 'Начните вводить название города'}
        value={input}
        onChange={(e) => {
          setInput(e.target.value)
          setOpen(true)
        }}
        onFocus={() => setOpen(true)}
        onKeyDown={onKey}
        autoComplete="off"
      />
      {open && (
        <div className="absolute z-20 left-0 right-0 mt-1 rounded-lg border border-border-subtle bg-card shadow-lg max-h-72 overflow-auto">
          {query.isLoading && (
            <div className="px-4 py-3 text-sm text-text-tertiary">Поиск…</div>
          )}
          {!query.isLoading && items.length === 0 && (
            <div className="px-4 py-3 text-sm text-text-tertiary">
              {debounced ? 'Ничего не найдено' : 'Начните вводить название города'}
            </div>
          )}
          {items.map((c, idx) => (
            <button
              key={c.id}
              type="button"
              onMouseEnter={() => setHighlighted(idx)}
              onClick={() => choose(c)}
              className={cn(
                'w-full text-left px-4 py-2 flex items-baseline justify-between gap-3 transition-colors',
                idx === highlighted ? 'bg-state-active-bg' : 'hover:bg-page-bg',
              )}
            >
              <span className="text-sm text-text-primary">{c.name}</span>
              <span className="text-xs text-text-tertiary">{c.region}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
