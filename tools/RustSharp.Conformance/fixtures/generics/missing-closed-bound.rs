trait Mark {}
fn keep<T: Mark>(value: T) -> T { value }
fn main() { keep(1); }
