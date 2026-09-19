struct Pair<T> { first: T, second: T }
fn trace(value: i32) -> i32 { println!("{}", value); value }
fn identity<T>(value: T) -> T { value }
fn main() {
    let result = identity(Pair { second: trace(2), first: trace(1) });
    println!("{}", result.first);
    println!("{}", result.second);
}
