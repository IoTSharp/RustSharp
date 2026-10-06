pub struct Pair { pub left: i32, pub right: bool }

pub fn make() -> Pair { Pair { left: 42, right: true } }

pub fn consume(value: Pair) -> i32 { value.left }

fn main() {}
