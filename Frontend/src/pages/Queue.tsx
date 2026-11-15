import { Link, useNavigate } from 'react-router-dom';
import { useImportQueueStore } from '../stores/importQueue';
import { ImportQueue } from '../components/ImportQueue';
import ui from '../components/ui.module.css';
import styles from './Queue.module.css';

/**
 * Bug report #103: the conversion queue as its own page (nav "Queue") — it
 * used to sit permanently beside the import form and got in the way. Review
 * hands off to /import?review=<id>, which loads the draft into the paste tab.
 * The queue component self-hides when empty (fine in a sidebar, blank as a
 * page) — the page carries its own empty state.
 */
export default function Queue() {
  const navigate = useNavigate();
  const hasDrafts = useImportQueueStore((s) => s.items.length > 0);
  return (
    <div className={styles.page}>
      {hasDrafts ? (
        <ImportQueue onReview={(id) => navigate(`/import?review=${id}`)} />
      ) : (
        <div className={ui.card}>
          <p className={ui.muted}>
            Nothing in the queue. Paste a chord page on the{' '}
            <Link to="/import">Import page</Link> (Convert tab) to start a conversion.
          </p>
        </div>
      )}
    </div>
  );
}
