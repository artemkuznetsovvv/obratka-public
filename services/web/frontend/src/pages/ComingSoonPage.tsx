import { AppLayout } from '@/layouts/AppLayout'
import { Card } from '@/components/ui/card'

interface ComingSoonPageProps {
  title: string
  breadcrumbs: { label: string }[]
}

export default function ComingSoonPage({ title, breadcrumbs }: ComingSoonPageProps) {
  return (
    <AppLayout breadcrumbs={breadcrumbs}>
      <h1 className="text-h1 text-text-primary mb-8">{title}</h1>
      <Card className="p-12 text-center">
        <p className="text-text-secondary">
          Раздел в разработке. После завершения MVP пользовательского флоу
          появится здесь.
        </p>
      </Card>
    </AppLayout>
  )
}
