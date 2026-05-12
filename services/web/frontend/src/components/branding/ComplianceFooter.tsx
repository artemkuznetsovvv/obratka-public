import { ShieldCheck } from 'lucide-react'

export function ComplianceFooter() {
  return (
    <footer className="mt-8 flex w-full max-w-[480px] flex-col items-center text-center">
      <div className="mb-4 flex items-center justify-center gap-2 text-caption uppercase text-text-tertiary">
        <ShieldCheck size={14} />
        <span>Соответствие 152-ФЗ. Защищённое соединение HTTPS</span>
      </div>
      <div className="flex flex-wrap justify-center gap-x-6 gap-y-2 text-caption uppercase text-text-tertiary">
        <a className="hover:text-text-secondary transition-colors" href="#">Политика конфиденциальности</a>
        <a className="hover:text-text-secondary transition-colors" href="#">Условия использования</a>
        <a className="hover:text-text-secondary transition-colors" href="#">Поддержка</a>
      </div>
      <p className="mt-6 text-caption uppercase text-text-tertiary/70">© Обратка 2026</p>
    </footer>
  )
}
