import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { AuthCard } from '../components/AuthCard';
import { TextField } from '../components/TextField';
import { useAuthStore } from '../stores/auth';
import { ApiError } from '../api/types';
import styles from '../components/AuthForm.module.css';

interface FieldErrors {
  identity?: string;
  password?: string;
}

export default function Login() {
  const login = useAuthStore((s) => s.login);
  const token = useAuthStore((s) => s.token);
  const navigate = useNavigate();
  const location = useLocation();
  const from = (location.state as { from?: string } | null)?.from ?? '/';

  // ST-35 A1: a single identifier field — username or email.
  const [identity, setIdentity] = useState('');
  const [password, setPassword] = useState('');
  const [errors, setErrors] = useState<FieldErrors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [inFlight, setInFlight] = useState(false);

  // Already signed in? Head to the dashboard (or the originally-requested page).
  useEffect(() => {
    if (token) navigate(from, { replace: true });
  }, [token, from, navigate]);

  function clearError(field: keyof FieldErrors) {
    setErrors((prev) => ({ ...prev, [field]: undefined }));
  }

  function handleSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setFormError(null);

    const next: FieldErrors = {};
    if (!identity.trim()) next.identity = 'Username or email is required.';
    if (!password) next.password = 'Password is required.';
    setErrors(next);
    if (next.identity || next.password) return;

    // ST-35 A1: route the identifier to the right field — anything with an @ is an email.
    const id = identity.trim();
    const creds = id.includes('@') ? { email: id, password } : { username: id, password };
    setInFlight(true);
    login(creds)
      .catch((err: unknown) => {
        if (err instanceof ApiError) {
          if (err.status === 0) setFormError('Cannot reach server. Check your connection and try again.');
          else if (err.status === 401) setFormError('Invalid username/email or password.');
          else setFormError(err.message);
        } else {
          setFormError('Something went wrong. Please try again.');
        }
      })
      .finally(() => setInFlight(false));
  }

  return (
    <AuthCard
      title="Welcome back"
      subtitle="Sign in to your song library"
      footer={
        <>
          New here?{' '}
          <Link to="/register" className={styles.link}>
            Create an account
          </Link>
        </>
      }
    >
      <form className={styles.form} onSubmit={handleSubmit} noValidate>
        {formError && (
          <p className={styles.formError} role="alert">
            {formError}
          </p>
        )}
        <TextField
          label="Username or email"
          type="text"
          value={identity}
          onChange={(v) => {
            setIdentity(v);
            clearError('identity');
          }}
          error={errors.identity}
          placeholder="your username or email"
          autoComplete="username"
          autoFocus
        />
        <TextField
          label="Password"
          type="password"
          value={password}
          onChange={(v) => {
            setPassword(v);
            clearError('password');
          }}
          error={errors.password}
          placeholder="Your password"
          autoComplete="current-password"
        />
        <button type="submit" className={styles.primaryBtn} disabled={inFlight}>
          {inFlight ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
    </AuthCard>
  );
}
