struct Pair<T, U>(T, U);
fn swap<T, U>(pair: Pair<T, U>) -> Pair<U, T> { Pair(pair.1, pair.0) }
fn main() {
    let result = swap(Pair::<i32, bool>(42, true));
    println!("{}", result.1);
    println!("{}", result.0);
}
