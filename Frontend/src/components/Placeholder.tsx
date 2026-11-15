import styles from './Placeholder.module.css';

interface PlaceholderProps {
  title: string;
  nextTask: string;
}

/** Shared placeholder body for the ST-15 skeleton pages. */
export function Placeholder({ title, nextTask }: PlaceholderProps) {
  return (
    <section className={styles.wrap} aria-labelledby="placeholder-title">
      <h1 className={styles.title} id="placeholder-title">
        {title}
      </h1>
      <p className={styles.hint}>coming in {nextTask}</p>
    </section>
  );
}
