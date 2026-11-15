import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { AuthCard } from '../components/AuthCard';
import { TextField } from '../components/TextField';
import { PasswordStrength } from '../components/PasswordStrength';
import { useAuthStore } from '../stores/auth';
import { ApiError } from '../api/types';
import styles from '../components/AuthForm.module.css';

interface FieldErrors {
  username?: string;
  email?: string;
  password?: string;
  confirm?: string;
}

function isValidEmail(email: string): boolean {
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email);
}

// ST-35 A1: must match the server's rule (AuthEndpoints.IsValidUsername).
function isValidUsername(username: string): boolean {
  return /^[A-Za-z0-9._-]{3,32}$/.test(username);
}

export default function Register() {
  const register = useAuthStore((s) => s.register);
  const token = useAuthStore((s) => s.token);
  const navigate = useNavigate();

  // ST-35 A1: username is the primary login name; email stays as the contact address.
  const [username, setUsername] = useState('');
  const [email, setEmail] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [errors, setErrors] = useState<FieldErrors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [inFlight, setInFlight] = useState(false);

  // Auto-login on success: register() sets the token, which sends us to the dashboard.
  useEffect(() => {
    if (token) navigate('/', { replace: true });
  }, [token, navigate]);

  function clearError(field: keyof FieldErrors) {
    setErrors((prev) => ({ ...prev, [field]: undefined }));
  }

  function handleSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setFormError(null);

    const next: FieldErrors = {};
    if (!username.trim()) next.username = 'Username is required.';
    else if (!isValidUsername(username.trim())) next.username = '3-32 characters: letters, numbers, dot, dash or underscore.';
    if (!email.trim()) next.email = 'Email is required.';
    else if (!isValidEmail(email)) next.email = 'Enter a valid email address.';
    if (password.length < 8) next.password = 'Password must be at least 8 characters.';
    if (!confirm) next.confirm = 'Please confirm your password.';
    else if (confirm !== password) next.confirm = 'Passwords do not match.';
    setErrors(next);
    if (next.username || next.email || next.password || next.confirm) return;

    setInFlight(true);
    register({
      username: username.trim(),
      email: email.trim(),
      displayName: displayName.trim() || undefined,
      password,
    })
      .catch((err: unknown) => {
        if (err instanceof ApiError) {
          if (err.status === 0) setFormError('Cannot reach server. Check your connection and try again.');
          else if (err.status === 409) setFormError(err.fields?.username ?? err.fields?.email ?? 'Already registered.');
          else setFormError(err.message);
        } else {
          setFormError('Something went wrong. Please try again.');
        }
      })
      .finally(() => setInFlight(false));
  }

  return (
    <AuthCard
      title="Create account"
      subtitle="Set up your chord library"
      footer={
        <>
          Already have an account?{' '}
          <Link to="/login" className={styles.link}>
            Sign in
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
          label="Username"
          type="text"
          value={username}
          onChange={(v) => {
            setUsername(v);
            clearError('username');
          }}
          error={errors.username}
          placeholder="How you'll sign in"
          autoComplete="username"
          autoFocus
        />
        <TextField
          label="Email"
          type="email"
          value={email}
          onChange={(v) => {
            setEmail(v);
            clearError('email');
          }}
          error={errors.email}
          placeholder="you@example.com"
          autoComplete="email"
        />
        <TextField
          label="Display name (optional)"
          value={displayName}
          onChange={setDisplayName}
          placeholder="How should we greet you?"
          autoComplete="name"
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
          placeholder="At least 8 characters"
          autoComplete="new-password"
          hint={<PasswordStrength password={password} />}
        />
        <TextField
          label="Confirm password"
          type="password"
          value={confirm}
          onChange={(v) => {
            setConfirm(v);
            clearError('confirm');
          }}
          error={errors.confirm}
          placeholder="Repeat your password"
          autoComplete="new-password"
        />
        <button type="submit" className={styles.primaryBtn} disabled={inFlight}>
          {inFlight ? 'Creating…' : 'Create account'}
        </button>
      </form>
    </AuthCard>
  );
}
