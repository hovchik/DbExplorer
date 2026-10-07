-- Sample database for the render harness: a few tables with foreign keys, a view, a function, a procedure,
-- a trigger, some rows, and roles with grants so the Security tab has something to show.
-- Run as a superuser:  PGPASSWORD=postgres psql -h localhost -U postgres -f tools/DbExplorer.UiRender/seed-postgres.sql
DROP DATABASE IF EXISTS uirender_shop;
CREATE DATABASE uirender_shop;
\c uirender_shop
CREATE TABLE customers (id serial PRIMARY KEY, name text NOT NULL, email text UNIQUE, created_at timestamptz NOT NULL DEFAULT now(), is_active boolean NOT NULL DEFAULT true);
CREATE TABLE products (id serial PRIMARY KEY, sku text UNIQUE NOT NULL, name text NOT NULL, price numeric(10,2) NOT NULL, stock int NOT NULL DEFAULT 0);
CREATE TABLE orders (id serial PRIMARY KEY, customer_id int NOT NULL REFERENCES customers(id), ordered_at timestamptz NOT NULL DEFAULT now(), status text NOT NULL DEFAULT 'new', note text);
CREATE TABLE order_items (id serial PRIMARY KEY, order_id int NOT NULL REFERENCES orders(id) ON DELETE CASCADE, product_id int NOT NULL REFERENCES products(id), quantity int NOT NULL, unit_price numeric(10,2) NOT NULL);
CREATE INDEX ix_orders_customer ON orders(customer_id);
CREATE INDEX ix_order_items_order ON order_items(order_id);
CREATE INDEX ix_order_items_product ON order_items(product_id);
INSERT INTO customers(name, email) SELECT 'Customer ' || g, 'customer' || g || '@example.com' FROM generate_series(1, 40) g;
INSERT INTO products(sku, name, price, stock) SELECT 'SKU-' || lpad(g::text, 4, '0'), 'Product ' || g, (g * 3.5)::numeric(10,2), g * 7 FROM generate_series(1, 25) g;
INSERT INTO orders(customer_id, status, note) SELECT 1 + (g % 40), (ARRAY['new','paid','shipped','cancelled'])[1 + g % 4], CASE WHEN g % 5 = 0 THEN NULL ELSE 'Order note ' || g END FROM generate_series(1, 120) g;
INSERT INTO order_items(order_id, product_id, quantity, unit_price) SELECT 1 + (g % 120), 1 + (g % 25), 1 + (g % 4), ((g % 25) + 1) * 3.5 FROM generate_series(1, 400) g;
CREATE VIEW v_order_totals AS
  SELECT o.id AS order_id, c.name AS customer, o.status, sum(oi.quantity * oi.unit_price) AS total
  FROM orders o JOIN customers c ON c.id = o.customer_id JOIN order_items oi ON oi.order_id = o.id
  GROUP BY o.id, c.name, o.status;
CREATE FUNCTION order_total(p_order_id int) RETURNS numeric LANGUAGE plpgsql AS $$
DECLARE t numeric;
BEGIN
  SELECT sum(quantity * unit_price) INTO t FROM order_items WHERE order_id = p_order_id;
  RETURN coalesce(t, 0);
END $$;
CREATE PROCEDURE mark_shipped(p_order_id int) LANGUAGE plpgsql AS $$
BEGIN
  UPDATE orders SET status = 'shipped' WHERE id = p_order_id;
END $$;
CREATE SEQUENCE invoice_seq;
CREATE FUNCTION trg_touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW.ordered_at := NEW.ordered_at; RETURN NEW; END $$;
CREATE TRIGGER orders_touch BEFORE UPDATE ON orders FOR EACH ROW EXECUTE FUNCTION trg_touch();
-- The routine debugger needs pldbgapi (skip this line when the extension is not installed on the server).
CREATE EXTENSION IF NOT EXISTS pldbgapi;
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'shop_app') THEN CREATE ROLE shop_app LOGIN PASSWORD 'shop_app'; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'shop_readers') THEN CREATE ROLE shop_readers NOLOGIN; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'reporting') THEN CREATE ROLE reporting LOGIN PASSWORD 'reporting'; END IF;
END $$;
GRANT shop_readers TO shop_app, reporting;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO shop_readers;
GRANT INSERT, UPDATE ON orders, order_items TO shop_app;
GRANT EXECUTE ON FUNCTION order_total(int) TO shop_readers;
