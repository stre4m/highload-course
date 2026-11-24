CREATE TABLE IF NOT EXISTS users (
    id uuid PRIMARY KEY,
    first_name varchar(100) NOT NULL,
    second_name varchar(100) NOT NULL,
    birthdate date NOT NULL,
    gender varchar(50) NOT NULL,
    biography varchar(2000) NOT NULL,
    city varchar(100) NOT NULL,
    password_hash text NOT NULL
);
