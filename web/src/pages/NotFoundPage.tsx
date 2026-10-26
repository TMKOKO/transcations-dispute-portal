import { Link } from 'react-router'
import { EmptyState } from '../components/ui'
import { useDocumentTitle } from '../lib/useDocumentTitle'

export function NotFoundPage() {
  useDocumentTitle('Page not found')
  return (
    <EmptyState title="We couldn't find that page">
      <Link to="/transactions" className="text-brand-700 underline">
        Go to your transactions
      </Link>
    </EmptyState>
  )
}
