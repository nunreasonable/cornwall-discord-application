use std::{collections::hash_map::RandomState, hash::BuildHasher, time::Instant};

/// Numero ao acaso em 0..n, sem crate de rand: o RandomState ja traz chaves
/// aleatorias a cada instancia. Serve para sorteio, NUNCA para segredo.
pub fn below(n: u64) -> u64 {
    RandomState::new().hash_one(Instant::now()) % n.max(1)
}
