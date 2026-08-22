using System;

namespace AiCodeAgent.Providers.Backend;

/// <summary>
/// Inline template bodies for the bundled backend primitives. Keeping them
/// as C# string constants avoids the need to ship a templates directory on
/// disk and makes the catalog self-contained.
/// </summary>
internal static class BackendTemplates
{
    public const string AuthJs = @"const jwt = require('jsonwebtoken');

function authMiddleware(req, res, next) {
  const header = req.headers['authorization'] || '';
  const token = header.startsWith('Bearer ') ? header.slice(7) : null;
  if (!token) return next();
  try {
    req.user = jwt.verify(token, process.env.JWT_SECRET);
  } catch {
    // ignore invalid tokens; treat as anonymous
  }
  next();
}

module.exports = { authMiddleware };
";

    public const string AuthRoutesJs = @"const express = require('express');
const jwt = require('jsonwebtoken');
const router = express.Router();

router.post('/login', (req, res) => {
  // TODO: validate credentials against your user store
  const user = { id: 1, email: req.body.email };
  const token = jwt.sign(user, process.env.JWT_SECRET, {
    expiresIn: (process.env.JWT_EXPIRES_HOURS || 24) + 'h'
  });
  res.json({ token, user });
});

router.post('/register', (req, res) => {
  // TODO: create user in your user store
  const user = { id: 1, email: req.body.email };
  const token = jwt.sign(user, process.env.JWT_SECRET, {
    expiresIn: (process.env.JWT_EXPIRES_HOURS || 24) + 'h'
  });
  res.json({ token, user });
});

module.exports = router;
";

    public const string DbJs = @"const { Pool } = require('pg');

const pool = new Pool({
  connectionString: process.env.DB_CONNECTION_STRING,
  max: parseInt(process.env.DB_POOL_SIZE || '10', 10)
});

async function query(text, params) {
  return pool.query(text, params);
}

async function runMigrations(db) {
  // TODO: apply SQL migration files in order (e.g. ./migrations/*.sql)
}

async function seed(db) {
  // TODO: insert seed data
}

module.exports = { pool, query, runMigrations, seed };
";

    public const string StorageJs = @"const express = require('express');
const fs = require('fs');
const path = require('path');
const router = express.Router();

const driver = process.env.STORAGE_DRIVER || 'local';
const localDir = process.env.STORAGE_LOCAL_DIR || './uploads';

if (driver === 'local') {
  fs.mkdirSync(localDir, { recursive: true });
}

router.post('/upload', (req, res) => {
  // TODO: handle multipart upload and persist via configured driver
  res.status(501).json({ error: 'not implemented' });
});

router.get('/download/:key', (req, res) => {
  // TODO: stream file from configured driver
  res.status(501).json({ error: 'not implemented' });
});

module.exports = router;
";

    public const string EmailJs = @"const nodemailer = require('nodemailer');

const transporter = nodemailer.createTransport({
  host: process.env.SMTP_HOST,
  port: parseInt(process.env.SMTP_PORT || '587', 10),
  auth: process.env.SMTP_USER
    ? { user: process.env.SMTP_USER, pass: process.env.SMTP_PASS }
    : undefined
});

async function sendMail(to, subject, html) {
  return transporter.sendMail({
    from: process.env.EMAIL_FROM || 'noreply@example.com',
    to,
    subject,
    html
  });
}

module.exports = { transporter, sendMail };
";

    public const string PaymentsJs = @"const express = require('express');
const router = express.Router();

// Provider-agnostic placeholder. Wire to stripe/paddle/lemonsqueezy based on
// PAYMENTS_PROVIDER. Always verify webhook signatures with PAYMENTS_WEBHOOK_SECRET.
router.post('/checkout', (req, res) => {
  // TODO: create a checkout session for the authenticated user
  res.status(501).json({ error: 'not implemented' });
});

router.post('/webhook', (req, res) => {
  // TODO: verify signature, dispatch event, update subscription state
  res.status(501).json({ error: 'not implemented' });
});

module.exports = router;
";
}