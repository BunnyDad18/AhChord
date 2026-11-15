import { Link } from 'react-router-dom';
import styles from './NotFound.module.css';

export function NotFound() {
  return (
    <div className={styles.wrap}>
      <h1 className={styles.code}>404</h1>
      <p className={styles.msg}>That page doesn&apos;t exist.</p>
      <Link to="/" className={styles.link}>
        Back to home
      </Link>
    </div>
  );
}
