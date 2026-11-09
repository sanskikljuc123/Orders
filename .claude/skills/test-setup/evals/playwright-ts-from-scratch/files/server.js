const express = require('express');
const crypto = require('crypto');
const app = express();
app.use(express.json());
app.use(express.static('public'));
let todos = [];
app.get('/api/todos', (req, res) => res.json(todos));
app.post('/api/todos', (req, res) => {
  const title = (req.body.title || '').trim();
  if (!title) return res.status(400).json({ error: 'Title is required' });
  const todo = { id: crypto.randomUUID(), title };
  todos.push(todo);
  res.status(201).json(todo);
});
app.delete('/api/todos/:id', (req, res) => {
  const before = todos.length;
  todos = todos.filter(t => t.id !== req.params.id);
  res.status(todos.length < before ? 204 : 404).end();
});
app.listen(process.env.PORT || 3000);
